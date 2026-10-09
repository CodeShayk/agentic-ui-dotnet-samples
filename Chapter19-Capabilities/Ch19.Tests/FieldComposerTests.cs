using Beacon.Client.Core;
using Ch19.Beacon.Adaptive.Capabilities;
using Ch19.Beacon.Adaptive.Planning;
using Ch19.Beacon.Agent.Capabilities;
using Ch19.Tests.Infrastructure;
using Microsoft.Extensions.AI;

namespace Ch19.Tests;

/// <summary>
/// Beacon Field's composer, driven through <see cref="ComposerModel"/> with a fake <see cref="IMediaCapture"/>
/// that returns media generated in code. The device path is exercised only through the fake.
/// </summary>
public sealed class FieldComposerTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task<FeaturePlan> FieldPlanAsync(AgentHost host)
    {
        var discovery = new CapabilityDiscovery(host.CreateClient(), CapabilityDiscovery.DocumentFor(host.Endpoint));
        return FeaturePlanner.ForField(AgentProfile.From((await discovery.DiscoverAsync(Cancel)).Capabilities));
    }

    [Fact]
    public async Task On_the_full_agent_Photo_captures_and_the_photo_reaches_the_model()
    {
        await using var host = new AgentHost(CapabilityProfile.Full);
        var capture = new FakeMediaCapture(TestMedia.Photo);
        var composer = new ComposerModel(await FieldPlanAsync(host), capture, BeaconFeature.PhotoInput, BeaconFeature.VoiceNote);
        using var conversation = ClientStack.Conversation(host.CreateClient(), host.Endpoint);

        Assert.Equal(("Photo", InputAction.Capture), (composer.Button(BeaconFeature.PhotoInput).Text, composer.Button(BeaconFeature.PhotoInput).Action));
        await composer.PressAsync(BeaconFeature.PhotoInput, Cancel);
        composer.Draft = "Nameplate for WO-1001.";
        var message = composer.TakeMessage()!;
        await conversation.SendAsync(message, cancellationToken: Cancel);

        Assert.Equal([BeaconFeature.PhotoInput], capture.Requests);
        Assert.Equal("image/png", Assert.Single(message.Contents.OfType<DataContent>()).MediaType);
        Assert.Contains("your photo (image/png", conversation.View.Messages.OfType<AssistantMessageView>().Last().Text, StringComparison.Ordinal);
        Assert.Empty(composer.Tray.Items);
    }

    [Fact]
    public async Task On_the_reduced_agent_Voice_note_becomes_a_template_and_the_device_is_never_asked()
    {
        await using var host = new AgentHost(CapabilityProfile.Reduced);
        var capture = new FakeMediaCapture(TestMedia.Recording);
        var composer = new ComposerModel(await FieldPlanAsync(host), capture, BeaconFeature.PhotoInput, BeaconFeature.VoiceNote);

        var voice = composer.Button(BeaconFeature.VoiceNote);
        Assert.Equal(("Describe the noise", InputAction.InsertTemplate), (voice.Text, voice.Action));
        Assert.Equal("Type nameplate", composer.Button(BeaconFeature.PhotoInput).Text);
        Assert.Equal(2, composer.Notes.Count);

        await composer.PressAsync(BeaconFeature.VoiceNote, Cancel);

        Assert.Empty(capture.Requests);
        Assert.Equal("The unit makes a ____ noise when ____, lasting about ____ seconds.", composer.Draft);
    }

    [Fact]
    public async Task On_an_agent_that_says_nothing_Voice_note_is_offered_with_a_caveat()
    {
        var capture = new FakeMediaCapture(TestMedia.Recording);
        var composer = new ComposerModel(FeaturePlanner.ForField(AgentProfile.Undeclared), capture, BeaconFeature.PhotoInput, BeaconFeature.VoiceNote);

        var voice = composer.Button(BeaconFeature.VoiceNote);
        Assert.True(voice.IsVisible);
        Assert.Contains("hasn't said whether it listens", voice.Caption, StringComparison.Ordinal);

        await composer.PressAsync(BeaconFeature.VoiceNote, Cancel);

        Assert.Equal([BeaconFeature.VoiceNote], capture.Requests);
        Assert.Equal("audio/wav", Assert.Single(composer.Tray.Items).MediaType);
        Assert.Null(composer.LastRefusal);
    }

    [Fact]
    public async Task A_cancelled_capture_leaves_the_tray_and_the_draft_alone()
    {
        var composer = new ComposerModel(FeaturePlanner.ForField(AgentProfile.Undeclared), new FakeMediaCapture(), BeaconFeature.PhotoInput);
        composer.Draft = "Unit EQ-501";

        await composer.PressAsync(BeaconFeature.PhotoInput, Cancel);

        Assert.Empty(composer.Tray.Items);
        Assert.Equal("Unit EQ-501", composer.Draft);
        Assert.Null(new ComposerModel(FeaturePlanner.ForField(AgentProfile.Undeclared), new FakeMediaCapture()).TakeMessage());
    }
}
