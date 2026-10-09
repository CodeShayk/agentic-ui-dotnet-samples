using System.Net;
using System.Text;
using Ch19.Beacon.Adaptive.Capabilities;
using Ch19.Beacon.Adaptive.Planning;
using Ch19.Tests.Infrastructure;

namespace Ch19.Tests;

/// <summary>An omitted capability is unknown, a false one is unsupported, and the plan treats them differently.</summary>
public sealed class UnknownVersusUnsupportedTests
{
    private static AgentProfile Profile(string json) => AgentProfile.From(CapabilityReader.Read(json).Capabilities);

    private const string SaysNo =
        """{"tools":{"supported":true,"clientProvided":false},"reasoning":{"supported":false},"multimodal":{"input":{"image":false,"audio":false,"pdf":false}}}""";

    private const string SaysYes =
        """{"tools":{"supported":true,"clientProvided":true},"reasoning":{"supported":true},"multimodal":{"input":{"image":true,"audio":true,"pdf":true}}}""";

    [Fact]
    public void A_partial_declaration_leaves_the_rest_unknown_not_unsupported()
    {
        var profile = Profile("""{"multimodal":{"input":{"image":true}}}""");

        Assert.Equal(Support.Supported, profile.ImageInput);
        Assert.Equal(Support.Unknown, profile.AudioInput); // omitted, so not "can't hear"
        Assert.Equal(Support.Unknown, profile.Reasoning);
        Assert.Equal(Support.Unknown, profile.ClientTools);
    }

    [Fact]
    public void The_Desk_offers_unknown_inputs_as_unconfirmed_and_explains_unsupported_ones()
    {
        var unknown = FeaturePlanner.ForDesk(AgentProfile.Undeclared);
        var no = FeaturePlanner.ForDesk(Profile(SaysNo));
        var yes = FeaturePlanner.ForDesk(Profile(SaysYes));

        Assert.Equal(Presentation.OfferUnconfirmed, unknown[BeaconFeature.PhotoInput].Presentation);
        Assert.NotNull(unknown[BeaconFeature.PhotoInput].Note);
        Assert.Equal(Presentation.Explain, no[BeaconFeature.PhotoInput].Presentation);
        Assert.Contains("can't read photos", no[BeaconFeature.PhotoInput].Note, StringComparison.Ordinal);
        Assert.Equal(Presentation.Offer, yes[BeaconFeature.PhotoInput].Presentation);
        Assert.Null(yes[BeaconFeature.PhotoInput].Note);
    }

    [Fact]
    public void The_Field_replaces_unsupported_inputs_with_text_templates()
    {
        var no = FeaturePlanner.ForField(Profile(SaysNo));

        Assert.Equal(Presentation.Degrade, no[BeaconFeature.VoiceNote].Presentation);
        Assert.Equal("Describe the noise", no[BeaconFeature.VoiceNote].Label);
        Assert.StartsWith("The unit makes a ____ noise", no[BeaconFeature.VoiceNote].Template, StringComparison.Ordinal);
        Assert.Equal(Presentation.Degrade, no[BeaconFeature.PhotoInput].Presentation);
    }

    [Fact]
    public void Reasoning_waits_for_the_stream_when_unknown_and_hides_when_declared_absent()
    {
        var unknown = FeaturePlanner.ForDesk(AgentProfile.Undeclared);
        var no = FeaturePlanner.ForDesk(Profile(SaysNo));
        var encrypted = FeaturePlanner.ForDesk(Profile("""{"reasoning":{"supported":true,"encrypted":true}}"""));

        Assert.Equal(Presentation.WhenStreamed, unknown[BeaconFeature.ReasoningPanel].Presentation);
        Assert.False(unknown.ShowsReasoning(reasoningArrived: false));
        Assert.True(unknown.ShowsReasoning(reasoningArrived: true));

        Assert.Equal(Presentation.Hide, no[BeaconFeature.ReasoningPanel].Presentation);
        Assert.False(no.ShowsReasoning(reasoningArrived: false));

        // The stream is authoritative: reasoning that arrives anyway is shown, and flagged for developers.
        Assert.True(no.ShowsReasoning(reasoningArrived: true));
        Assert.True(no.ReasoningContradictsDeclaration(reasoningArrived: true));
        Assert.False(unknown.ReasoningContradictsDeclaration(reasoningArrived: true));

        Assert.Equal(Presentation.Explain, encrypted[BeaconFeature.ReasoningPanel].Presentation);
    }

    [Fact]
    public void Screen_tools_are_declared_unless_the_agent_said_it_wont_use_them()
    {
        Assert.True(FeaturePlanner.ForDesk(AgentProfile.Undeclared).SendsScreenTools);
        Assert.True(FeaturePlanner.ForDesk(Profile(SaysYes)).SendsScreenTools);
        Assert.False(FeaturePlanner.ForDesk(Profile(SaysNo)).SendsScreenTools);
        Assert.False(FeaturePlanner.ForDesk(Profile("""{"tools":{"supported":false,"clientProvided":true}}""")).SendsScreenTools);
    }

    [Fact]
    public void The_tray_takes_unconfirmed_media_refuses_unsupported_media_and_honors_the_declared_limit()
    {
        var unknown = new AttachmentTray(FeaturePlanner.ForField(AgentProfile.Undeclared));
        var no = new AttachmentTray(FeaturePlanner.ForField(Profile(SaysNo)));
        var small = new AttachmentTray(FeaturePlanner.ForField(Profile($$$$"""{"multimodal":{"input":{"image":true}},"custom":{"{{{{AgentProfile.AttachmentLimitsKey}}}}":{"image":64}}}""")));

        Assert.Null(unknown.TryAdd(TestMedia.Recording));
        Assert.Contains("can't listen to recordings", no.TryAdd(TestMedia.Recording), StringComparison.Ordinal);
        Assert.Contains("accepts up to", small.TryAdd(TestMedia.Photo), StringComparison.Ordinal);
        Assert.Contains("doesn't send", unknown.TryAdd(new MediaAttachment("notes.txt", "text/plain", new byte[3])), StringComparison.Ordinal);
        Assert.Single(unknown.Items);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "", DiscoveryOutcome.NotOffered)]
    [InlineData(HttpStatusCode.InternalServerError, "", DiscoveryOutcome.Unreachable)]
    [InlineData(HttpStatusCode.OK, """{"transport":{"streaming":"yes"}}""", DiscoveryOutcome.Rejected)]
    [InlineData(HttpStatusCode.OK, """{"reasoning":{"supported":true},"newGroupFrom2027":{"x":1}}""", DiscoveryOutcome.Declared)]
    public async Task Discovery_never_fails_a_client_it_only_learns_less(HttpStatusCode status, string body, DiscoveryOutcome expected)
    {
        using var http = new HttpClient(new Canned(status, body));
        var discovery = new CapabilityDiscovery(http, new Uri("http://agent.example/agent/capabilities"));

        var result = await discovery.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expected == DiscoveryOutcome.Declared, result.Capabilities is not null);
        if (expected == DiscoveryOutcome.Declared)
        {
            Assert.Contains(result.Notes, n => n.Contains("/newGroupFrom2027", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task An_unreachable_agent_is_treated_as_one_that_declared_nothing()
    {
        using var http = new HttpClient(new Throws());
        var result = await new CapabilityDiscovery(http, new Uri("http://agent.example/agent/capabilities")).DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DiscoveryOutcome.Unreachable, result.Outcome);
        Assert.Equal(AgentProfile.Undeclared, AgentProfile.From(result.Capabilities));
    }

    private sealed class Canned(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class Throws : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
    }
}
