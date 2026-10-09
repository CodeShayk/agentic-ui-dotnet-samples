using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using Ch23.Beacon.Security.Approvals;
using Ch23.Beacon.Security.History;
using Ch23.Beacon.Security.Logging;
using Microsoft.Extensions.Logging;

namespace Ch23.Beacon.Security.Output;

/// <summary>Everything the outgoing stream needs to know about the run it belongs to.</summary>
public sealed class RunScope
{
    public required ApprovalRun Run { get; init; }

    public required RunLease Lease { get; init; }

    public required RunPlan Plan { get; init; }

    public required IApprovalStore Approvals { get; init; }

    public required IApprovalAudit Audit { get; init; }

    public required ApprovalOptions ApprovalOptions { get; init; }

    /// <summary>Cancels the model call when the run outlives its budget.</summary>
    public required CancellationTokenSource RunTimeout { get; init; }

    public required TimeSpan RunTimeoutLimit { get; init; }
}

/// <summary>
/// The last stage before the wire. It does what Chapter 22's <c>RecordingEventStream</c> did (folds the run's
/// events into the transcript, commits before the terminal event leaves and releases the thread's lease,
/// restates the conversation when the client's copy diverged) and what Chapter 14's <c>ApprovalEventStream</c>
/// did (opens the approvals the run proposed and writes their interrupts, withdraws the ones nobody saw). It
/// adds stripping what clients don't need and reporting a run timeout as one.
/// </summary>
public static class SecureEventStream
{
    public const string RunTimeoutCode = "RUN_TIMEOUT";

    public static async IAsyncEnumerable<BaseEvent> RunAsync(
        IAsyncEnumerable<BaseEvent> events,
        RunScope scope,
        TimeProvider time,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var run = scope.Run;
        var conversation = scope.Lease.Conversation;
        var recorder = new TranscriptRecorder(conversation.Messages
            .OfType<AGUIAssistantMessage>()
            .SelectMany(m => m.ToolCalls ?? [])
            .Select(c => c.Id));
        var frontendTools = scope.Plan.Input.Tools?.Select(t => t.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var frontendCalls = 0;
        var committed = false;

        // The run has started: the client's new messages join the transcript, and frontend calls it didn't
        // answer are no longer awaited.
        conversation.PendingClientCalls.Clear();
        conversation.Append(scope.Plan.AcceptedMessages);

        try
        {
            await foreach (var evt in events.WithCancellation(cancellationToken))
            {
                // rawEvent echoes the provider's update (model IDs, provider extras). Clients don't need it.
                evt.RawEvent = null;
                recorder.Observe(evt);

                if (evt is ToolCallStartEvent start && frontendTools.Contains(start.ToolCallName))
                {
                    frontendCalls++;
                }

                var outgoing = evt;
                switch (evt)
                {
                    case RunErrorEvent when scope.RunTimeout.IsCancellationRequested:
                        SecurityLog.RunTimedOut(logger, run.RunId, run.ThreadId, run.UserId, (int)scope.RunTimeoutLimit.TotalSeconds);
                        outgoing = new RunErrorEvent
                        {
                            Code = RunTimeoutCode,
                            Message = $"Beacon stopped this run after {scope.RunTimeoutLimit.TotalSeconds:0} seconds.",
                        };
                        break;

                    case RunFinishedEvent finished:
                        // A run that also left a frontend call with the client showed nobody its proposals
                        // (AGUI.Server 1.0.0 ends it with success); they are withdrawn below (Chapter 14).
                        if (frontendCalls == 0)
                        {
                            OpenProposals(finished);
                        }

                        Commit(RunEnding.Finished);
                        committed = true;

                        // Let the client's next request in before this one has finished writing.
                        scope.Lease.Dispose();
                        break;
                }

                if (outgoing is RunErrorEvent error)
                {
                    Commit(RunEnding.Failed, (error.Message, error.Code));
                    committed = true;
                    scope.Lease.Dispose();
                }

                yield return outgoing;

                // The client showed a history the server didn't write, so the run restates the server's (Chapter 22).
                if (evt is RunStartedEvent && scope.Plan.Diverged)
                {
                    yield return new MessagesSnapshotEvent { Messages = [.. conversation.Messages] };
                }
            }
        }
        finally
        {
            if (!committed)
            {
                // No terminal event went out. If the client left (Stop, a closed tab), the server stopped the run;
                // anything else broke it under the server. Either way, keep what the user was shown.
                Commit(cancellationToken.IsCancellationRequested ? RunEnding.Cancelled : RunEnding.Broken);
            }

            foreach (var withdrawn in scope.Approvals.Withdraw(run.UserId, run.ThreadId, run.RunId))
            {
                scope.Audit.Record(ApprovalAuditEntry.For(ApprovalEvent.Withdrawn, withdrawn, time.GetUtcNow(), run) with
                {
                    Reason = frontendCalls > 0 ? "proposed beside a frontend tool call; never shown" : "the run ended before it was shown",
                });
            }

            scope.Lease.Dispose();
        }

        // The SDK's interrupts for these calls are replaced by the server's own, from its records.
        void OpenProposals(RunFinishedEvent finished)
        {
            var proposed = scope.Approvals.ProposedIn(run.UserId, run.ThreadId, run.RunId);
            if (proposed.Count == 0)
            {
                return;
            }

            var expiresAt = time.GetUtcNow() + scope.ApprovalOptions.TimeToLive;
            var others = (finished.Outcome as RunFinishedInterruptOutcome)?.Interrupts?
                .Where(i => !proposed.Any(p => p.Id == i.Id)) ?? [];
            var opened = proposed.Select(p => scope.Approvals.Open(p, expiresAt)).ToList();

            finished.Outcome = new RunFinishedInterruptOutcome
            {
                Interrupts = [.. others, .. opened.Select(a => ApprovalInterrupts.For(a, scope.ApprovalOptions))],
            };

            foreach (var approval in opened)
            {
                scope.Audit.Record(ApprovalAuditEntry.For(ApprovalEvent.Requested, approval, time.GetUtcNow(), run));
            }
        }

        void Commit(RunEnding ending, (string Message, string? Code)? error = null)
        {
            conversation.Append(recorder.Messages);
            conversation.Recorded(run.RunId, time.GetUtcNow(), ending, error);
            foreach (var (callId, name) in recorder.UnansweredCalls.Where(c => frontendTools.Contains(c.Name)))
            {
                conversation.PendingClientCalls[callId] = name;
            }
        }
    }
}
