using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using Ch22.Beacon.Agent.Conversations;

namespace Ch22.Beacon.Agent.Runs;

/// <summary>Everything the outgoing stream needs to know about its run.</summary>
public sealed class RecordingScope
{
    public required IConversationStore Store { get; init; }

    public required RunLease Lease { get; init; }

    public required RunAgentInput Input { get; init; }

    public required RunPlan Plan { get; init; }

    public required RunScope Run { get; init; }

    /// <summary>Events that go out right after <c>RUN_STARTED</c>: the branch restated, a compaction notice.</summary>
    public required IReadOnlyList<BaseEvent> Restatements { get; init; }

    public required TimeProvider Time { get; init; }
}

/// <summary>
/// The last stage before the wire. It restates the branch after <c>RUN_STARTED</c> when the plan says the
/// client needs it, writes every event to the thread's log as it goes out, and records the run before the
/// terminal event leaves, so a client that sends its next request the moment it reads <c>RUN_FINISHED</c>
/// finds the run recorded and the thread free. A stream that stops early is recorded with what it sent.
/// </summary>
public static class RecordingEventStream
{
    public static async IAsyncEnumerable<BaseEvent> RunAsync(
        IAsyncEnumerable<BaseEvent> events,
        RecordingScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var conversation = scope.Lease.Conversation;
        var started = new RunRecord
        {
            RunId = scope.Input.RunId,
            ParentRunId = scope.Plan.Base?.RunId,
            StartedAt = scope.Time.GetUtcNow(),
            Input = [.. scope.Plan.NewMessages],
            State = scope.Input.State,
        };

        var knownCalls = scope.Plan.Branch.Concat(scope.Plan.NewMessages).OfType<AGUIAssistantMessage>().SelectMany(m => m.ToolCalls ?? []).Select(c => c.Id);
        var recorder = new TranscriptRecorder(scope.Input.State, knownCalls);
        var committed = false;

        await using var log = await scope.Store.BeginRunAsync(conversation, started, cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                recorder.Observe(evt);

                // The log's copy of RUN_STARTED carries the run's input, normalized to what the run added: with it,
                // the log alone can rebuild the run (the serialization concept's "exact agent input").
                await log.AppendAsync(evt is RunStartedEvent runStarted ? WithInput(runStarted, scope) : evt, cancellationToken).ConfigureAwait(false);

                if (evt is RunFinishedEvent or RunErrorEvent)
                {
                    await log.CompleteAsync(Record(started, recorder, scope), cancellationToken).ConfigureAwait(false);
                    committed = true;
                    scope.Lease.Dispose();
                }

                yield return evt;

                if (evt is RunStartedEvent)
                {
                    foreach (var restated in scope.Restatements)
                    {
                        await log.AppendAsync(restated, cancellationToken).ConfigureAwait(false);
                        yield return restated;
                    }
                }
            }
        }
        finally
        {
            if (!committed)
            {
                // No terminal event went out. If the client left (Stop, a closed tab, a dropped connection), the
                // server stopped the run: cancelled. Anything else broke the stream under the server. Either way,
                // keep what the client was shown; the request is gone, so the write ignores its token.
                var ending = cancellationToken.IsCancellationRequested ? RunEnding.Cancelled : RunEnding.Broken;
                await log.CompleteAsync(Record(started, recorder, scope, ending), CancellationToken.None).ConfigureAwait(false);
            }

            scope.Lease.Dispose();
        }
    }

    private static RunRecord Record(RunRecord started, TranscriptRecorder recorder, RecordingScope scope, RunEnding unterminated = RunEnding.Broken) => started with
    {
        EndedAt = scope.Time.GetUtcNow(),
        Ending = recorder.Ending ?? unterminated,
        ProtocolVersion = recorder.ProtocolVersion,
        Outcome = recorder.Outcome,
        Error = recorder.Error,
        ErrorCode = recorder.ErrorCode,
        Output = [.. recorder.Messages],
        State = recorder.State,
        PendingInputs = recorder.Outcome is RunFinishedInterruptOutcome ? [.. scope.Run.PendingInputs] : [],
    };

    private static RunStartedEvent WithInput(RunStartedEvent started, RecordingScope scope) => new()
    {
        ThreadId = started.ThreadId,
        RunId = started.RunId,
        ParentRunId = started.ParentRunId,
        ProtocolVersion = started.ProtocolVersion,
        Timestamp = started.Timestamp,
        Input = new RunAgentInput
        {
            ThreadId = scope.Input.ThreadId,
            RunId = scope.Input.RunId,
            ParentRunId = scope.Plan.Base?.RunId,
            Messages = [.. scope.Plan.NewMessages],
            State = scope.Input.State,
        },
    };
}
