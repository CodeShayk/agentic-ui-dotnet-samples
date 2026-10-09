using System.Runtime.CompilerServices;
using AGUI.Abstractions;

namespace Ch14.Beacon.Agent.Approvals;

/// <summary>
/// The last stage before the wire. When a run that proposed approvals finishes, it opens them and writes
/// their interrupts itself, so every approval a client sees is one the server recorded, with the server's
/// message, expiry and schema. Proposals the client never saw are withdrawn.
/// </summary>
public static class ApprovalEventStream
{
    public static async IAsyncEnumerable<BaseEvent> RunAsync(
        IAsyncEnumerable<BaseEvent> events,
        ApprovalRun run,
        RunAgentInput input,
        IApprovalStore approvals,
        IApprovalAudit audit,
        ApprovalOptions options,
        TimeProvider time,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var frontendTools = input.Tools?.Select(t => t.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var frontendCalls = 0;

        try
        {
            await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (evt is ToolCallStartEvent start && frontendTools.Contains(start.ToolCallName))
                {
                    frontendCalls++;
                }

                // Opened before RUN_FINISHED is written, so a client that answers the moment it reads it finds the approval open.
                if (evt is RunFinishedEvent finished && frontendCalls == 0)
                {
                    OpenProposals(finished);
                }

                yield return evt;
            }
        }
        finally
        {
            // A run that failed or was stopped showed nobody its proposals. So did a run that also left a
            // frontend call with the client: AGUI.Server 1.0.0 ends that run with success, and the call
            // will meet the check at the tool instead.
            foreach (var withdrawn in approvals.Withdraw(run.ThreadId, run.RunId))
            {
                audit.Record(ApprovalAuditEntry.For(ApprovalEvent.Withdrawn, withdrawn, time.GetUtcNow(), run.RunId, run.ClientId) with
                {
                    Reason = frontendCalls > 0 ? "proposed beside a frontend tool call; never shown to a dispatcher" : "the run ended before it was shown",
                });
            }
        }

        void OpenProposals(RunFinishedEvent finished)
        {
            var proposed = approvals.ProposedIn(run.ThreadId, run.RunId);
            if (proposed.Count == 0)
            {
                return;
            }

            // The SDK's interrupts for these calls are replaced by the server's own. (When the client
            // declared tools, AGUI.Server 1.0.0 writes none at all and finishes with success.)
            var expiresAt = time.GetUtcNow() + options.TimeToLive;
            var others = (finished.Outcome as RunFinishedInterruptOutcome)?.Interrupts?
                .Where(i => !proposed.Any(p => p.Id == i.Id)) ?? [];
            var opened = proposed.Select(p => approvals.Open(p, expiresAt)).ToList();

            finished.Outcome = new RunFinishedInterruptOutcome
            {
                Interrupts = [.. others, .. opened.Select(a => ApprovalInterrupts.For(a, options))],
            };

            foreach (var approval in opened)
            {
                audit.Record(ApprovalAuditEntry.For(ApprovalEvent.Requested, approval, time.GetUtcNow(), run.RunId, run.ClientId));
            }
        }
    }
}
