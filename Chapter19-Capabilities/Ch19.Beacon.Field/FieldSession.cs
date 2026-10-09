using AGUI.Client;
using Beacon.Client.Core;
using Ch19.Beacon.Adaptive.Capabilities;
using Ch19.Beacon.Adaptive.Planning;
using Microsoft.Extensions.Logging;

namespace Ch19.Beacon.Field;

/// <summary>
/// The technician's session: discovery when the app opens (and on New chat), Beacon Field's plan,
/// and a conversation. The Field declares no frontend tools in this chapter.
/// </summary>
public sealed class FieldSession(HttpClient http, Uri agentEndpoint, ILoggerFactory loggers) : IDisposable
{
    public CapabilityDiscoveryResult? Discovery { get; private set; }

    public FeaturePlan Plan { get; private set; } = FeaturePlanner.ForField(AgentProfile.Undeclared);

    public AgentConversation? Conversation { get; private set; }

    public async Task DiscoverAsync(CancellationToken cancellationToken = default)
    {
        Discovery = await new CapabilityDiscovery(http, CapabilityDiscovery.DocumentFor(agentEndpoint))
            .DiscoverAsync(cancellationToken);
        Plan = FeaturePlanner.ForField(AgentProfile.From(Discovery.Capabilities));

        Conversation?.Dispose();
        var tap = WireEventTap.ForHttp(http, agentEndpoint.AbsoluteUri);
        Conversation = new AgentConversation(
            new AGUIChatClient(new AGUIChatClientOptions { Transport = tap }),
            new ConversationOptions { Tap = tap, Logger = loggers.CreateLogger<AgentConversation>() });
    }

    public void Dispose() => Conversation?.Dispose();
}
