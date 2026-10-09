using AGUI.Client;
using Beacon.Client.Core;
using Ch19.Beacon.Adaptive.Capabilities;
using Ch19.Beacon.Adaptive.Planning;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ch19.Beacon.Desk.Session;

/// <summary>
/// One dispatcher's session with the agent: what discovery found, the plan made from it, and a
/// conversation built to match the plan. A new chat discovers again, so a redeployed agent is
/// noticed at the next natural boundary rather than in the middle of a turn.
/// </summary>
public sealed partial class DeskSession(HttpClient http, Uri agentEndpoint, ILoggerFactory? loggers = null) : IDisposable
{
    public const string HttpClientName = "BeaconAgent";

    private readonly ILogger _logger = loggers?.CreateLogger<DeskSession>() ?? NullLogger<DeskSession>.Instance;

    /// <summary>Raised after discovery, a new conversation or a screen-tool call, and on every conversation change. Any thread.</summary>
    public event EventHandler? Changed;

    public CapabilityDiscoveryResult? Discovery { get; private set; }

    /// <summary>The plan. Until discovery finishes it is the plan for an agent that declared nothing.</summary>
    public FeaturePlan Plan { get; private set; } = FeaturePlanner.ForDesk(AgentProfile.Undeclared);

    public AgentConversation? Conversation { get; private set; }

    public AttachmentTray Attachments { get; private set; } = new(FeaturePlanner.ForDesk(AgentProfile.Undeclared));

    /// <summary>The work order the agent opened on this screen with <c>open_work_order</c>.</summary>
    public string? OpenWorkOrderId { get; private set; }

    /// <summary>Reads the agent's capabilities and starts a conversation that fits them.</summary>
    public async Task DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var discovery = new CapabilityDiscovery(http, CapabilityDiscovery.DocumentFor(agentEndpoint));
        Discovery = await discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        foreach (var note in Discovery.Notes)
        {
            LogDiscoveryNote(_logger, Discovery.Outcome, note); // for developers; the Desk carries on regardless
        }

        Plan = FeaturePlanner.ForDesk(AgentProfile.From(Discovery.Capabilities));
        StartConversation();
    }

    /// <summary>Sends the composer's text with whatever is in the attachment tray.</summary>
    public async Task SendAsync(string text)
    {
        if (Conversation is not { View.IsBusy: false } conversation || conversation.View.OpenInterrupts.Any())
        {
            return;
        }

        var message = Attachments.ToMessage(text);
        Attachments.Clear();
        await conversation.SendAsync(message).ConfigureAwait(false);
    }

    private void StartConversation()
    {
        if (Conversation is not null)
        {
            Conversation.Changed -= OnConversationChanged;
            Conversation.Dispose();
        }

        var tap = WireEventTap.ForHttp(http, agentEndpoint.AbsoluteUri);
        Conversation = new AgentConversation(
            new AGUIChatClient(new AGUIChatClientOptions { Transport = tap }),
            new ConversationOptions
            {
                Tap = tap,
                // The screen tool exists in this conversation only when the plan offers it, so an agent
                // that declared clientProvided: false is never sent a tool it said it won't use.
                Tools = Plan.SendsScreenTools ? [ScreenTools.OpenWorkOrder(this)] : [],
                Logger = loggers?.CreateLogger<AgentConversation>(),
            });
        Conversation.Changed += OnConversationChanged;
        Attachments = new AttachmentTray(Plan);
        OpenWorkOrderId = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void ShowWorkOrder(string workOrderId)
    {
        OpenWorkOrderId = workOrderId;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lets the UI announce a change it made itself (an attachment added or removed).</summary>
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (Conversation is not null)
        {
            Conversation.Changed -= OnConversationChanged;
            Conversation.Dispose();
        }
    }

    [LoggerMessage(EventId = 1921, Level = LogLevel.Information, Message = "Capability discovery ({Outcome}): {Note}")]
    private static partial void LogDiscoveryNote(ILogger logger, DiscoveryOutcome outcome, string note);
}
