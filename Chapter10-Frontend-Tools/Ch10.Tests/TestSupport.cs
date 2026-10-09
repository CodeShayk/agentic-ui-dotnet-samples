using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Ch10.Field.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;

namespace Ch10.Tests;

/// <summary>Records every run a client makes through it: the input it sent and the events it got back.</summary>
public sealed class RecordingTransport(IAGUITransport inner) : IAGUITransport
{
    public List<(RunAgentInput Input, List<BaseEvent> Events)> Runs { get; } = [];

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<BaseEvent> events = [];
        Runs.Add((input, events));
        await foreach (var evt in inner.SendAsync(input, cancellationToken))
        {
            events.Add(evt);
            yield return evt;
        }
    }
}

/// <summary>Chapter 10's agent in-process, and the client stack Beacon's apps put in front of it.</summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Ch10.Beacon.Agent.Program> _factory = new WebApplicationFactory<Ch10.Beacon.Agent.Program>()
        .WithWebHostBuilder(host => host.UseSetting("AI:ScriptedChunkDelayMilliseconds", "0"));

    public HttpClient CreateHttpClient() => _factory.CreateClient();

    /// <summary>transport → tap → recorder → AGUIChatClient → AgentConversation, with the given frontend tools.</summary>
    public (AgentConversation Conversation, RecordingTransport Transport, IChatClient Client) CreateConversation(IList<AITool> tools)
    {
        var tap = WireEventTap.ForHttp(_factory.CreateClient(), "/agent");
        var recorder = new RecordingTransport(tap);
        var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = recorder });
        var conversation = new AgentConversation(client, new ConversationOptions { Tools = tools, Tap = tap });
        return (conversation, recorder, client);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

/// <summary>A consent prompt that answers from a script and remembers what it was asked.</summary>
public sealed class ScriptedConsent(params ConsentDecision[] answers) : IConsentPrompt
{
    private readonly Queue<ConsentDecision> _answers = new(answers);

    public List<ConsentRequest> Asked { get; } = [];

    public Task<ConsentDecision> AskAsync(ConsentRequest request, CancellationToken cancellationToken)
    {
        Asked.Add(request);
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : ConsentDecision.Deny);
    }
}

public sealed class FakePermissions(PermissionOutcome outcome = PermissionOutcome.Granted) : IDevicePermissions
{
    public List<DeviceCapability> Checked { get; } = [];

    public Task<PermissionOutcome> EnsureAsync(DeviceCapability capability, CancellationToken cancellationToken)
    {
        Checked.Add(capability);
        return Task.FromResult(outcome);
    }
}

public sealed class FakeLocation(DeviceLocation? reading) : ILocationSource
{
    public int Reads { get; private set; }

    public Task<DeviceLocation?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(reading);
    }
}

public sealed class FakeCamera(CapturedPhoto? photo, bool supported = true) : ICameraCapture
{
    public int Opened { get; private set; }

    public bool IsSupported => supported;

    public Task<CapturedPhoto?> CaptureAsync(CancellationToken cancellationToken)
    {
        Opened++;
        return Task.FromResult(photo);
    }
}

public sealed class FakeLabelReader(string? text) : IPartLabelReader
{
    public List<LabelReadRequest> Requests { get; } = [];

    public Task<LabelReading?> ReadAsync(LabelReadRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(text is null ? null : new LabelReading(text, "manual"));
    }
}

/// <summary>Beacon Field's tools over fakes.</summary>
public sealed record FieldRig(
    FieldTools Tools, ScriptedConsent Consent, SessionConsent Session, FakePermissions Permissions,
    FakeLocation Location, FakeCamera Camera, FakeLabelReader Labels, JobAttachments Attachments)
{
    /// <summary>Priya at Harbor View Dental's door: about 32 meters from the address on file.</summary>
    public static readonly DeviceLocation AtHarborView = new(43.071834, -70.762240, 24.6, new DateTimeOffset(2026, 11, 16, 9, 2, 0, TimeSpan.FromHours(-5)));

    public static FieldRig Create(
        ConsentDecision[]? consent = null,
        PermissionOutcome permission = PermissionOutcome.Granted,
        DeviceLocation? location = null,
        CapturedPhoto? photo = null,
        bool cameraSupported = true,
        string? label = "CAP-45-5")
    {
        var prompt = new ScriptedConsent(consent ?? [ConsentDecision.AllowOnce]);
        var session = new SessionConsent(prompt);
        var permissions = new FakePermissions(permission);
        var place = new FakeLocation(location ?? AtHarborView);
        var camera = new FakeCamera(photo ?? new CapturedPhoto(@"C:\photos\a.jpg", "image/jpeg", 482_113), cameraSupported);
        var labels = new FakeLabelReader(label);
        var attachments = new JobAttachments();
        return new FieldRig(new FieldTools(place, camera, labels, permissions, session, attachments), prompt, session, permissions, place, camera, labels, attachments);
    }
}
