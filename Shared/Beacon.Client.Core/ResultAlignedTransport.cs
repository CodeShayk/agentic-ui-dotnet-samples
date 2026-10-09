using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;

namespace Beacon.Client.Core;

/// <summary>
/// Lets what an agent streams while its tools run reach the view as it arrives. <c>AGUIChatClient</c>
/// 1.0.0 holds back every update that follows a <c>TOOL_CALL_END</c> until that call's
/// <c>TOOL_CALL_RESULT</c> arrives, so the state, activity and step events an agent sends while its
/// tools work would all appear at once, when the tools are done. This transport delays each
/// <c>TOOL_CALL_END</c> instead, releasing it just before the call's result (or before the run's
/// terminal event, for a call the run leaves to the client), so the events in between flow through as
/// they arrive.
/// </summary>
/// <remarks>
/// Put it between the <see cref="WireEventTap"/> and <c>AGUIChatClient</c>:
/// <c>new AGUIChatClient(new AGUIChatClientOptions { Transport = new ResultAlignedTransport(tap) })</c>.
/// The tap sees every event on time, so the view still shows a tool call as running from its real
/// end; only the chat client's bookkeeping is shifted, and it never sees a call without its end.
/// Chapter 13 introduces it.
/// </remarks>
/// <param name="inner">The transport underneath, normally the tap.</param>
public sealed class ResultAlignedTransport(IAGUITransport inner) : IAGUITransport
{
    /// <inheritdoc />
    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<ToolCallEndEvent> held = []; // in arrival order

        await foreach (var evt in inner.SendAsync(input, cancellationToken).ConfigureAwait(false))
        {
            switch (evt)
            {
                case ToolCallEndEvent end:
                    held.Add(end);
                    continue; // released with its result

                case ToolCallResultEvent result when held.FindIndex(e => e.ToolCallId == result.ToolCallId) is var index and >= 0:
                    yield return held[index];
                    held.RemoveAt(index);
                    break;

                case RunFinishedEvent or RunErrorEvent:
                    // Calls without a result (a frontend tool, an approval) end before the run does.
                    foreach (var end in held)
                    {
                        yield return end;
                    }

                    held.Clear();
                    break;
            }

            yield return evt;
        }

        foreach (var end in held)
        {
            yield return end; // a stream that broke off without a terminal event
        }
    }
}
