using AGUI.Client;
using Beacon.Client.Core;
using Ch08.Field.Core.Connection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch08.Field.Core;

/// <summary>
/// The client stack every Chapter 8 client uses, assembled in one place: the agent's transport, the
/// retry layer, Chapter 7's tap, <c>AGUIChatClient</c> and the conversation.
/// </summary>
public sealed class FieldClient : IDisposable
{
    private FieldClient(ResilientAgentTransport transport, WireEventTap tap, AGUIChatClient chatClient, AgentConversation conversation)
    {
        Transport = transport;
        Tap = tap;
        ChatClient = chatClient;
        Conversation = conversation;
    }

    /// <summary>The retry layer. Subscribe to <see cref="ResilientAgentTransport.ConnectionChanged"/> to show connection progress.</summary>
    public ResilientAgentTransport Transport { get; }

    /// <summary>Chapter 7's tap, above the retry layer, so it sees only the attempt that started.</summary>
    public WireEventTap Tap { get; }

    /// <summary>The SDK's chat client.</summary>
    public IChatClient ChatClient { get; }

    /// <summary>The conversation the UI renders.</summary>
    public AgentConversation Conversation { get; }

    /// <summary>Builds the stack over HTTP.</summary>
    /// <param name="http">The client to send runs with, normally from <see cref="FieldHttp.CreateClient"/>. The caller owns it.</param>
    /// <param name="endpoint">The agent's AG-UI endpoint.</param>
    /// <param name="options">Policy, timeouts and logging.</param>
    public static FieldClient Create(HttpClient http, Uri endpoint, FieldClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        return Create(new AGUIChatClientOptions(http, endpoint.ToString()).Transport, options);
    }

    /// <summary>Builds the stack over any transport: HTTP, an in-process agent or a test double.</summary>
    /// <param name="agentTransport">The transport that reaches the agent.</param>
    /// <param name="options">Policy, timeouts and logging.</param>
    public static FieldClient Create(IAGUITransport agentTransport, FieldClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(agentTransport);
        options ??= new FieldClientOptions();

        // agent transport → retry before the first event → tap → AGUIChatClient → conversation
        var resilient = new ResilientAgentTransport(agentTransport, options.Connection);
        var tap = new WireEventTap(resilient);
        var chatClient = new AGUIChatClient(new AGUIChatClientOptions { Transport = tap });
        var conversation = new AgentConversation(chatClient, new ConversationOptions
        {
            Tap = tap,
            TurnTimeout = options.TurnTimeout,
            Logger = options.Logger,
            TimeProvider = options.TimeProvider,
            ThreadId = options.ThreadId,
        });

        return new FieldClient(resilient, tap, chatClient, conversation);
    }

    /// <summary>Stops any turn in progress and releases the chat client.</summary>
    public void Dispose()
    {
        Conversation.Dispose();
        ChatClient.Dispose();
    }
}

/// <summary>Settings for <see cref="FieldClient"/>.</summary>
public sealed class FieldClientOptions
{
    /// <summary>Retry attempts and backoff before a run's first event.</summary>
    public ConnectionPolicy Connection { get; init; } = ConnectionPolicy.Default;

    /// <summary>The longest a turn may take, including the wait for a slow model's first event.</summary>
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Where the conversation logs failures.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>The clock for interrupt expiry; <see langword="null"/> uses the system clock.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>The thread to start on; <see langword="null"/> mints one.</summary>
    public string? ThreadId { get; init; }
}
