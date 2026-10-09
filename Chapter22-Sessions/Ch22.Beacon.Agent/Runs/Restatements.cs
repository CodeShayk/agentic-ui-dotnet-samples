using System.Text.Json;
using AGUI.Abstractions;
using Ch22.Beacon.Agent.Conversations;

namespace Ch22.Beacon.Agent.Runs;

/// <summary>
/// How the server restates what it holds. AG-UI's rule for restating history is that it goes out as snapshots:
/// <c>MESSAGES_SNAPSHOT</c> reconciles by ID and <c>STATE_SNAPSHOT</c> replaces, so a client that already holds
/// some of it ends up with the same result. Streaming the old text again would append to it instead.
/// </summary>
public static class Restatements
{
    /// <summary>The <c>CUSTOM</c> event that tells a client which messages the model now sees only as a summary.</summary>
    public const string CompactionEventName = "beacon.compaction";

    /// <summary>The <c>RUN_ERROR</c> code of a run that broke under the server before it could end.</summary>
    public const string BrokenRunCode = "RUN_INCOMPLETE";

    public static MessagesSnapshotEvent Branch(IEnumerable<AGUIMessage> messages) => new() { Messages = [.. messages] };

    public static CustomEvent Compaction(CompactionRecord compaction) => new()
    {
        Name = CompactionEventName,
        Value = JsonSerializer.SerializeToElement(compaction, ConversationJson.TypeInfo<CompactionRecord>()),
    };

    /// <summary>
    /// One run, restated for a client that opens the thread: the run's boundaries with its own IDs and the
    /// version it declared, the branch that ends with it as one <c>MESSAGES_SNAPSHOT</c> (sealed reasoning
    /// values included, since a snapshot that left them out would withdraw them), its state, the compaction in
    /// force and how it ended, interrupts included. Folded through a reducer, it gives the view that run left.
    /// </summary>
    public static IEnumerable<BaseEvent> Run(StoredConversation conversation, RunRecord run)
    {
        var branch = conversation.BranchMessages(run.RunId);

        yield return new RunStartedEvent
        {
            ThreadId = conversation.ThreadId,
            RunId = run.RunId,
            ParentRunId = run.ParentRunId,
            ProtocolVersion = run.ProtocolVersion, // as declared then; the endpoint stamps runs recorded without one
        };
        yield return Branch(branch);

        if (run.State is { } state && !(state.ValueKind == JsonValueKind.Object && !state.EnumerateObject().Any()))
        {
            yield return new StateSnapshotEvent { Snapshot = state };
        }

        if (conversation.CompactionFor(branch) is { } compaction)
        {
            yield return Compaction(compaction);
        }

        yield return run.Ending switch
        {
            RunEnding.Failed => new RunErrorEvent { Message = run.Error ?? "The run failed.", Code = run.ErrorCode },

            // The client left and the server stopped the run on purpose: a stop, not a failure.
            RunEnding.Cancelled => new RunFinishedEvent { ThreadId = conversation.ThreadId, RunId = run.RunId, Outcome = new RunFinishedCancelledOutcome() },

            // The stream broke, or the process stopped, under the run: it didn't end cleanly.
            RunEnding.Broken => new RunErrorEvent { Message = "The agent stopped before this run finished.", Code = BrokenRunCode },

            _ => new RunFinishedEvent { ThreadId = conversation.ThreadId, RunId = run.RunId, Outcome = run.Outcome ?? new RunFinishedSuccessOutcome() },
        };
    }
}
