// Adapted from Chapters 6 and 7 (Ch06.Tests, Ch07.Tests); each chapter's code is a self-contained checkpoint.
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Formatting;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch11.Tests;

/// <summary>
/// Streams captured with <c>curl -N</c> from this chapter's agent on port 5110 (see the Recordings folder),
/// read back with the SDK's own SSE reader, exactly as AGUIChatClient reads a live stream.
/// </summary>
public static class Recordings
{
    public const string PartCard = "part-card.sse";
    public const string InvalidCard = "invalid-card.sse";
    public const string UnknownComponent = "unknown-component.sse";
    public const string Schedule = "schedule.sse";
    public const string Faults = "faults.sse";

    public static async Task<IReadOnlyList<BaseEvent>> LoadAsync(string name)
    {
        await using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Recordings", name));
        List<BaseEvent> events = [];
        await foreach (var evt in new SseEventStreamFormatter().ReadAsync(file, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        return events;
    }

    /// <summary>A call's argument text as it streamed: its <c>TOOL_CALL_ARGS</c> deltas, in order.</summary>
    public static IReadOnlyList<string> ArgumentDeltas(IReadOnlyList<BaseEvent> events, string toolName)
    {
        var callId = events.OfType<ToolCallStartEvent>().Single(e => e.ToolCallName == toolName).ToolCallId;
        return [.. events.OfType<ToolCallArgsEvent>().Where(e => e.ToolCallId == callId).Select(e => e.Delta)];
    }
}

/// <summary>
/// An <see cref="IAGUITransport"/> that answers each run with the next recorded stream, so the real
/// AGUIChatClient, the WireEventTap above it and the conversation run unchanged. A gated transport
/// holds each event until the test releases it, which makes "what is on screen after this event" a
/// question with one answer.
/// </summary>
public sealed class ReplayTransport(params IReadOnlyList<BaseEvent>[] runs) : IAGUITransport
{
    private readonly SemaphoreSlim _gate = new(0);
    private int _next;
    private int _planned;

    public bool Gated { get; init; }

    public List<RunAgentInput> Inputs { get; } = [];

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Inputs.Add(input);
        if (_next >= runs.Length)
        {
            throw new InvalidOperationException($"No recorded run left for run {Inputs.Count}.");
        }

        foreach (var evt in runs[_next++])
        {
            if (Gated)
            {
                await _gate.WaitAsync(cancellationToken);
            }
            else
            {
                await Task.Yield();
            }

            yield return evt;
        }
    }

    /// <summary>Releases events of the first run up to and including the next one that matches.</summary>
    public void ReleaseThrough(Func<BaseEvent, bool> match)
    {
        var events = runs[0];
        var count = 0;
        while (_planned < events.Count)
        {
            count++;
            if (match(events[_planned++]))
            {
                break;
            }
        }

        _gate.Release(count);
    }

    public void ReleaseAll() => ReleaseThrough(_ => false);
}

/// <summary>
/// The client stack every Beacon client uses, over a replay transport: transport, tap, AGUIChatClient,
/// AgentConversation. It keeps every view the conversation published, so a test can ask what the
/// screen could have shown in the middle of a run.
/// </summary>
public sealed class Harness : IDisposable
{
    public Harness(ReplayTransport transport, bool tap = true)
    {
        Transport = transport;
        Tap = tap ? new WireEventTap(transport) : null;
        Client = new AGUIChatClient(new AGUIChatClientOptions { Transport = (IAGUITransport?)Tap ?? transport });
        Conversation = new AgentConversation(Client, new ConversationOptions { ThreadId = "thread-11", Tap = Tap });
        Conversation.Changed += (_, e) =>
        {
            lock (Published)
            {
                Published.Add(e.View);
            }
        };
    }

    public ReplayTransport Transport { get; }

    public WireEventTap? Tap { get; }

    public AGUIChatClient Client { get; }

    public AgentConversation Conversation { get; }

    /// <summary>Every view the conversation published, in order.</summary>
    public List<ConversationView> Published { get; } = [];

    public static async Task<Harness> ReplayAsync(string recording, string prompt, bool tap = true)
    {
        var harness = new Harness(new ReplayTransport(await Recordings.LoadAsync(recording)), tap);
        await harness.Conversation.SendAsync(new ChatMessage(ChatRole.User, prompt) { MessageId = "user-1" }, cancellationToken: TestContext.Current.CancellationToken);
        return harness;
    }

    public void Dispose()
    {
        Conversation.Dispose();
        Client.Dispose();
    }
}
