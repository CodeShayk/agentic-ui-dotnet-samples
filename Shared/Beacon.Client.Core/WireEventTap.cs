using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;

namespace Beacon.Client.Core;

/// <summary>
/// An <see cref="IAGUITransport"/> that shows every AG-UI event to an observer the moment it comes
/// off the wire, then passes it on unchanged to <c>AGUIChatClient</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>AGUIChatClient</c> 1.0.0 surfaces nothing for <c>TEXT_MESSAGE_START</c>, <c>TEXT_MESSAGE_END</c>,
/// <c>TOOL_CALL_START</c> or <c>TOOL_CALL_ARGS</c>, and holds a finished tool call back until its
/// result arrives. A client that wants to show a tool call while it runs, or a message as soon as it
/// starts, needs the wire events, and this tap provides them without replacing the chat client.
/// </para>
/// <para>
/// Observers are keyed by thread ID, so one tap (and one <c>AGUIChatClient</c>) can serve several
/// conversations. The observer runs synchronously inside the stream, on the reading thread: keep it
/// short. <see cref="AgentConversation"/> subscribes itself when you pass the tap in its options.
/// </para>
/// <para>
/// The tap changes nothing <c>AGUIChatClient</c> sees. To stop the chat client holding back what an
/// agent streams while its tools run, put a <see cref="ResultAlignedTransport"/> between the two.
/// </para>
/// </remarks>
/// <param name="inner">The transport that talks to the agent.</param>
public sealed class WireEventTap(IAGUITransport inner) : IAGUITransport
{
    private readonly ConcurrentDictionary<string, Action<BaseEvent>> _observers = new(StringComparer.Ordinal);

    /// <summary>Creates a tap over the SDK's HTTP transport (Server-Sent Events unless the handler negotiates protobuf).</summary>
    /// <param name="httpClient">The <see cref="HttpClient"/> to send runs with. The caller owns it.</param>
    /// <param name="endpoint">The agent's AG-UI endpoint.</param>
    public static WireEventTap ForHttp(HttpClient httpClient, string endpoint) =>
        new(new AGUIChatClientOptions(httpClient, endpoint).Transport);

    /// <summary>Shows every event of every run on <paramref name="threadId"/> to <paramref name="observer"/>.</summary>
    /// <param name="threadId">The thread to observe.</param>
    /// <param name="observer">Called once per event, before the chat client sees it.</param>
    /// <returns>Dispose it to stop observing.</returns>
    public IDisposable Observe(string threadId, Action<BaseEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(observer);

        _observers[threadId] = observer;
        return new Subscription(this, threadId, observer);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        _observers.TryGetValue(input.ThreadId, out var observer);

        await foreach (var evt in inner.SendAsync(input, cancellationToken).ConfigureAwait(false))
        {
            observer?.Invoke(evt);
            yield return evt;
        }
    }

    private sealed class Subscription(WireEventTap tap, string threadId, Action<BaseEvent> observer) : IDisposable
    {
        public void Dispose() =>
            tap._observers.TryRemove(new KeyValuePair<string, Action<BaseEvent>>(threadId, observer));
    }
}
