using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Ch13.Beacon.Desk.Progress;
using Microsoft.Extensions.AI;

namespace Ch13.Tests.Infrastructure;

/// <summary>
/// Beacon Desk's client stack over a replay: transport, tap, (optionally) the result-aligned transport,
/// AGUIChatClient and the conversation. It keeps every view the conversation published, and the
/// progress board each one shows.
/// </summary>
public sealed class Harness : IDisposable
{
    public Harness(ReplayTransport transport, bool aligned = true)
    {
        Transport = transport;
        Tap = new WireEventTap(transport);
        Client = new AGUIChatClient(new AGUIChatClientOptions
        {
            Transport = aligned ? new ResultAlignedTransport(Tap) : Tap,
        });
        Conversation = new AgentConversation(Client, new ConversationOptions { ThreadId = "t-13", Tap = Tap });
        Conversation.Changed += (_, e) =>
        {
            lock (Published)
            {
                Published.Add((e.Action, e.View));
            }
        };
    }

    public ReplayTransport Transport { get; }

    public WireEventTap Tap { get; }

    public AGUIChatClient Client { get; }

    public AgentConversation Conversation { get; }

    /// <summary>Every change, in order: the action and the view it produced.</summary>
    public List<(ConversationAction Action, ConversationView View)> Published { get; } = [];

    /// <summary>The visible progression: each distinct board, in the order the screen would show them.</summary>
    public IReadOnlyList<string> Frames()
    {
        List<string> frames = [];
        foreach (var (_, view) in Published)
        {
            var frame = ProgressBoard.From(view).Describe();
            if (frames.Count == 0 || frames[^1] != frame)
            {
                frames.Add(frame);
            }
        }

        return frames;
    }

    /// <summary>Replays the recorded diagnosis through a fresh stack.</summary>
    public static async Task<Harness> ReplayAsync(bool aligned = true, Action<Harness>? before = null)
    {
        var harness = new Harness(new ReplayTransport(await Recordings.LoadAsync(Recordings.Diagnose)), aligned);
        before?.Invoke(harness);
        await harness.Conversation.SendAsync(
            new ChatMessage(ChatRole.User, AgentHost.Diagnose) { MessageId = "user-1" },
            cancellationToken: TestContext.Current.CancellationToken);
        return harness;
    }

    public void Dispose()
    {
        Conversation.Dispose();
        Client.Dispose();
    }
}
