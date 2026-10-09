using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Formatting;

namespace Ch26.Tests.Unit.Infrastructure;

/// <summary>
/// An <see cref="IAGUITransport"/> that answers each run with the next recorded stream, so the real
/// WireEventTap, AGUIChatClient and AgentConversation above it run unchanged (Chapter 9's replay, one
/// recording per run). It records every <see cref="RunAgentInput"/> the client sent.
/// </summary>
public sealed class ReplayTransport(params IReadOnlyList<BaseEvent>[] runs) : IAGUITransport
{
    private readonly Queue<IReadOnlyList<BaseEvent>> _runs = new(runs);

    /// <summary>Every RunAgentInput the client sent, in order.</summary>
    public List<RunAgentInput> Inputs { get; } = [];

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Inputs.Add(input);
        if (!_runs.TryDequeue(out var events))
        {
            throw new InvalidOperationException($"No recorded run left for run {Inputs.Count}.");
        }

        foreach (var evt in events)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return WithRunIds(evt, input);
        }
    }

    // A real server echoes the client's thread and run IDs; the recording carries the capture's.
    private static BaseEvent WithRunIds(BaseEvent evt, RunAgentInput input) => evt switch
    {
        RunStartedEvent => new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId, ProtocolVersion = AGUIProtocol.Version },
        RunFinishedEvent finished => new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId, Outcome = finished.Outcome },
        _ => evt,
    };

    /// <summary>Reads a stream captured with <c>curl -N</c> back with the SDK's own SSE reader.</summary>
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
}
