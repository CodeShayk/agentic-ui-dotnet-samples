using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch18.Tests.Infrastructure;

/// <summary>
/// Beacon Desk's client stack against the agent in memory, built as the Desk's Program builds it:
/// the SDK's HTTP transport, the tap, the result-aligned transport, AGUIChatClient and the conversation.
/// </summary>
public sealed class DeskStack : IDisposable
{
    public DeskStack(AgentHost host, bool aligned = true)
    {
        var http = host.Client();
        Transport = new RecordingTransport(new AGUIChatClientOptions(http, new Uri(http.BaseAddress!, "/agent").ToString()).Transport);
        var tap = new WireEventTap(Transport);
        Chat = new AGUIChatClient(new AGUIChatClientOptions { Transport = aligned ? new ResultAlignedTransport(tap) : tap });
        Conversation = new AgentConversation(Chat, new ConversationOptions { ThreadId = "t-desk", Tap = tap });
        Conversation.Changed += (_, e) => Views.Enqueue(e.View);
    }

    public RecordingTransport Transport { get; }

    public AGUIChatClient Chat { get; }

    public AgentConversation Conversation { get; }

    /// <summary>Every view the conversation published, in order.</summary>
    public ConcurrentQueue<ConversationView> Views { get; } = new();

    public Task SendAsync(string text) =>
        Conversation.SendAsync(new ChatMessage(ChatRole.User, text) { MessageId = $"user-{Guid.NewGuid():N}" }, cancellationToken: Runs.Ct);

    public void Dispose()
    {
        Conversation.Dispose();
        Chat.Dispose();
    }
}

/// <summary>Passes runs through and keeps every input and the events each one produced.</summary>
public sealed class RecordingTransport(IAGUITransport inner) : IAGUITransport
{
    private readonly ConcurrentQueue<(RunAgentInput Input, List<BaseEvent> Events)> _runs = new();

    public IReadOnlyList<RunAgentInput> Inputs => [.. _runs.Select(r => r.Input)];

    public IReadOnlyList<IReadOnlyList<BaseEvent>> Events => [.. _runs.Select(r => (IReadOnlyList<BaseEvent>)r.Events)];

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<BaseEvent> events = [];
        _runs.Enqueue((input, events));
        await foreach (var evt in inner.SendAsync(input, cancellationToken))
        {
            events.Add(evt);
            yield return evt;
        }
    }
}

/// <summary>Builds a view by folding events through the real reducer, the way AgentConversation does.</summary>
public static class Views
{
    public static ConversationView Fold(params BaseEvent[] events) => Fold(ConversationView.Empty with { ThreadId = "t-1" }, events);

    public static ConversationView Fold(ConversationView view, params BaseEvent[] events)
    {
        foreach (var evt in events)
        {
            // What AGUIChatClient yields nothing for reaches the reducer through the tap.
            view = ConversationReducer.Reduce(view, evt is TextMessageStartEvent or TextMessageEndEvent or ToolCallStartEvent or ToolCallArgsEvent or ToolCallEndEvent
                ? new WireEventObserved(evt)
                : new UpdateReceived(new ChatResponseUpdate { RawRepresentation = evt }));
        }

        return view;
    }

    /// <summary>A conversation with nothing behind it, for rendering views that were dispatched into it.</summary>
    public sealed class NoAgent : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
