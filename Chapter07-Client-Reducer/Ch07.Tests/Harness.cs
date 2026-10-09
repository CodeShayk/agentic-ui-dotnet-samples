using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch07.Tests;

/// <summary>
/// The client stack every Beacon client uses, over a replay transport: transport, tap, AGUIChatClient,
/// AgentConversation. It also keeps every view the conversation published, so a test can ask what the
/// screen showed in the middle of a run.
/// </summary>
public sealed class Harness : IDisposable
{
    /// <summary>Beacon's "today": Monday, November 16, 2026, 9:00 in Portsmouth.</summary>
    public static readonly DateTimeOffset Today = new(2026, 11, 16, 9, 0, 0, TimeSpan.FromHours(-5));

    public Harness(ReplayTransport transport, bool tap = true, string threadId = "t-1", IList<AITool>? tools = null,
        Action<RunAgentInput, ConversationView>? configureInput = null, DateTimeOffset? now = null)
    {
        Transport = transport;
        Tap = tap ? new WireEventTap(transport) : null;
        Client = new AGUIChatClient(new AGUIChatClientOptions { Transport = (IAGUITransport?)Tap ?? transport });
        Conversation = new AgentConversation(Client, new ConversationOptions
        {
            ThreadId = threadId,
            Tap = Tap,
            Tools = tools ?? [],
            ConfigureInput = configureInput,
            TimeProvider = new FixedClock(now ?? Today),
        });
        Conversation.Changed += (_, e) =>
        {
            lock (Published)
            {
                Published.Add((e.Action, e.View));
            }
        };
    }

    public ReplayTransport Transport { get; }

    public WireEventTap? Tap { get; }

    public AGUIChatClient Client { get; }

    public AgentConversation Conversation { get; }

    /// <summary>Every change, in order: the action and the view it produced.</summary>
    public List<(ConversationAction Action, ConversationView View)> Published { get; } = [];

    public ConversationView View => Conversation.View;

    /// <summary>Replays recorded runs through a fresh client stack and sends one message.</summary>
    public static async Task<Harness> ReplayAsync(string recording, string prompt = "Hello", bool tap = true, string threadId = "t-1")
    {
        var harness = new Harness(new ReplayTransport(await Recordings.LoadAsync(recording)), tap, threadId);
        await harness.Conversation.SendAsync(new ChatMessage(ChatRole.User, prompt) { MessageId = "user-1" }, cancellationToken: TestContext.Current.CancellationToken);
        return harness;
    }

    public T Message<T>(string id) where T : MessageView =>
        Assert.IsType<T>(View.FindMessage(id));

    public void Dispose()
    {
        Conversation.Dispose();
        Client.Dispose();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
