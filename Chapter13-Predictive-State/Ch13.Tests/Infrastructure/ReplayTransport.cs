using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;

namespace Ch13.Tests.Infrastructure;

/// <summary>
/// An <see cref="IAGUITransport"/> that answers each run with the next recorded event stream, so the real
/// WireEventTap, AGUIChatClient and conversation service run unchanged. <see cref="BeforeEach"/> runs
/// just before an event is handed on: by then every earlier event has been fully processed, so a test
/// can look at what the screen showed at that moment.
/// </summary>
public sealed class ReplayTransport(params IReadOnlyList<BaseEvent>[] runs) : IAGUITransport
{
    private int _next;

    /// <summary>Every RunAgentInput the client sent, in order.</summary>
    public List<RunAgentInput> Inputs { get; } = [];

    /// <summary>Called with each event before it is yielded.</summary>
    public Action<BaseEvent>? BeforeEach { get; set; }

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Inputs.Add(input);
        if (_next >= runs.Length)
        {
            throw new InvalidOperationException($"No recorded run left for run {Inputs.Count}.");
        }

        foreach (var evt in runs[_next++])
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            BeforeEach?.Invoke(evt);
            yield return evt;
        }
    }
}
