using System.Text.Json;
using AGUI.Abstractions;

namespace Ch22.Beacon.Agent.Conversations;

/// <summary>How a run ended, as the server saw it.</summary>
public enum RunEnding
{
    /// <summary>The run ended with <c>RUN_FINISHED</c>; <see cref="RunRecord.Outcome"/> says how.</summary>
    Finished,

    /// <summary>The run ended with <c>RUN_ERROR</c>.</summary>
    Failed,

    /// <summary>
    /// The client left before a terminal event (Stop, a closed tab, a dropped connection), so the server stopped
    /// the run. A stop, not a failure: it is restated as <c>RUN_FINISHED</c> with the cancelled outcome.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The run broke under the server: its stream failed, or the process stopped mid-run and the next load found
    /// it open. It didn't end cleanly, so it is restated as <c>RUN_ERROR</c>.
    /// </summary>
    Broken,
}

/// <summary>
/// One run as the server keeps it: where it branched from, the messages it accepted, the messages it
/// produced, the state it ended with and how it ended. It is the run's events folded into what a client
/// would show, so the event log can be pruned without losing the conversation.
/// </summary>
public sealed record RunRecord
{
    /// <summary>The run's ID, from the client's <c>RunAgentInput</c>.</summary>
    public required string RunId { get; init; }

    /// <summary>The run whose end this run continued from; <see langword="null"/> for a run at the start of the thread.</summary>
    public string? ParentRunId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>The AG-UI version the run's <c>RUN_STARTED</c> declared, so a replay declares the same one.</summary>
    public string? ProtocolVersion { get; init; }

    public RunEnding Ending { get; init; }

    /// <summary>The <c>RUN_FINISHED</c> outcome, when <see cref="Ending"/> is <see cref="RunEnding.Finished"/>.</summary>
    public RunFinishedOutcome? Outcome { get; init; }

    /// <summary>The <c>RUN_ERROR</c> message and code, when <see cref="Ending"/> is <see cref="RunEnding.Failed"/>.</summary>
    public string? Error { get; init; }

    public string? ErrorCode { get; init; }

    /// <summary>What the run took in: the client's new messages and the server's record of any answers to interrupts.</summary>
    public IList<AGUIMessage> Input { get; init; } = [];

    /// <summary>
    /// What the run produced: assistant, tool and reasoning messages, exactly as they went out, each with the
    /// sealed reasoning value (<c>encryptedValue</c>) the run attached to it.
    /// </summary>
    public IList<AGUIMessage> Output { get; init; } = [];

    /// <summary>The shared state when the run ended: the input's state with the run's state events applied.</summary>
    public JsonElement? State { get; init; }

    /// <summary>The model calls behind this run's input interrupts. Server-only: never sent to a client.</summary>
    public IList<PendingInput> PendingInputs { get; init; } = [];

    /// <summary>True when the run stopped to ask and its interrupts are the ones a continuation must answer.</summary>
    public bool IsInterrupted => Outcome is RunFinishedInterruptOutcome { Interrupts.Count: > 0 };

    /// <summary>The run's messages in order: input, then output.</summary>
    public IEnumerable<AGUIMessage> Messages => Input.Concat(Output);
}

/// <summary>
/// The model call an input interrupt stands for. The call never goes on the wire (the interrupt does); the
/// server keeps it so that the answer can go back to the model as that call's result.
/// </summary>
public sealed record PendingInput(
    string InterruptId,
    string CallId,
    string ToolName,
    string ArgumentsJson,
    DateTimeOffset ExpiresAt);

/// <summary>
/// A summary that stands in, for the model, for every message up to and including <see cref="ThroughMessageId"/>
/// on any branch that contains that message. The messages themselves stay in the store and in the transcript.
/// </summary>
public sealed record CompactionRecord
{
    /// <summary>The last message the summary covers.</summary>
    public required string ThroughMessageId { get; init; }

    /// <summary>How many model-visible messages, from the start of the branch, the summary replaces.</summary>
    public required int SummarizedMessages { get; init; }

    public required string Summary { get; init; }

    public required int EstimatedTokensBefore { get; init; }

    public required int EstimatedTokensAfter { get; init; }

    /// <summary>The run that needed it.</summary>
    public required string RunId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>What the store knows about a thread without reading its runs: enough to list it.</summary>
public sealed record ThreadHeader
{
    public required string ThreadId { get; init; }

    /// <summary>The first work order the conversation mentioned, so the Desk can list conversations per job.</summary>
    public string? WorkOrderId { get; init; }

    /// <summary>The start of the first question, for the list.</summary>
    public string Title { get; init; } = string.Empty;

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The run that ended most recently: what a client opening the thread sees by default.</summary>
    public string? HeadRunId { get; init; }

    /// <summary>A run that started and hasn't been recorded yet. Set at load means the process stopped mid-run.</summary>
    public string? OpenRunId { get; init; }

    public int RunCount { get; init; }

    public IList<CompactionRecord> Compactions { get; init; } = [];
}

/// <summary>One line of the event log: an event as it went out, with the run it belongs to.</summary>
public sealed record StoredEvent(string RunId, DateTimeOffset At, JsonElement Event);
