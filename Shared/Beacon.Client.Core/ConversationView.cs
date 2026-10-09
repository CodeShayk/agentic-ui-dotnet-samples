using System.Collections.Immutable;
using System.Net;
using AGUI.Abstractions;

namespace Beacon.Client.Core;

/// <summary>
/// Everything a client renders about one conversation with an AG-UI agent, folded from the run's
/// events by <see cref="ConversationReducer"/>. Each property is a slice that one kind of component
/// reads: the transcript, the tool calls, the shared state, the run's progress.
/// </summary>
/// <remarks>
/// The view is immutable. Every change produces a new instance that shares the slices it didn't
/// touch, so a component can tell whether its slice changed by comparing references. Treat the
/// <see cref="System.Text.Json.Nodes.JsonNode"/> documents inside it as read-only too: the reducer
/// never mutates one it has published.
/// </remarks>
public sealed record ConversationView
{
    /// <summary>A conversation with no thread, messages or runs yet.</summary>
    public static ConversationView Empty { get; } = new();

    /// <summary>The AG-UI thread ID the client sends on every run.</summary>
    public string? ThreadId { get; init; }

    /// <summary>What the conversation is doing, from the user's point of view.</summary>
    public ConversationStatus Status { get; init; }

    /// <summary>Why the last turn failed, when <see cref="Status"/> is <see cref="ConversationStatus.Failed"/>.</summary>
    public RunFailure? Failure { get; init; }

    /// <summary>Every AG-UI run on this thread, oldest first. One turn can hold several runs.</summary>
    public ImmutableList<RunView> Runs { get; init; } = [];

    /// <summary>The conversation in order: user, assistant, tool, reasoning, activity and system messages.</summary>
    public ImmutableList<MessageView> Messages { get; init; } = [];

    /// <summary>Every tool call, in the order the agent opened them.</summary>
    public ImmutableList<ToolCallView> ToolCalls { get; init; } = [];

    /// <summary>The state the agent and the client share (<c>STATE_SNAPSHOT</c> and <c>STATE_DELTA</c>).</summary>
    public SharedStateView State { get; init; } = SharedStateView.Empty;

    /// <summary>Named phases of the runs (<c>STEP_STARTED</c> and <c>STEP_FINISHED</c>), oldest first.</summary>
    public ImmutableList<StepView> Steps { get; init; } = [];

    /// <summary>Subagent invocations announced with <c>SUBAGENT_STARTED</c>, oldest first.</summary>
    public ImmutableList<SubagentView> Subagents { get; init; } = [];

    /// <summary>Interrupts the agent raised, answered or not.</summary>
    public ImmutableList<InterruptView> Interrupts { get; init; } = [];

    /// <summary>The most recent <c>CUSTOM</c> events (up to 50), oldest first.</summary>
    public ImmutableList<CustomEvent> CustomEvents { get; init; } = [];

    /// <summary>The most recent <c>RAW</c> events (up to 50), oldest first. Never derive behavior from them.</summary>
    public ImmutableList<RawEvent> RawEvents { get; init; } = [];

    /// <summary>The IDs of reasoning spans (<c>REASONING_START</c>) that haven't ended.</summary>
    public ImmutableHashSet<string> OpenReasoningSpans { get; init; } = [];

    /// <summary>What the last <c>MESSAGES_SNAPSHOT</c> did to the transcript, if one arrived.</summary>
    public ReconciliationSummary? LastReconciliation { get; init; }

    /// <summary>
    /// Things the reducer skipped and why: a patch that didn't apply, a delta for an unknown activity,
    /// an encrypted value for an unknown entity. The most recent 50, oldest first.
    /// </summary>
    public ImmutableList<string> Warnings { get; init; } = [];

    /// <summary>Open chunk streams (<c>*_CHUNK</c>), keyed by kind and lane. Reducer bookkeeping.</summary>
    internal ImmutableDictionary<string, string> OpenChunks { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>True while a turn is in progress: Send and New chat should be unavailable.</summary>
    public bool IsBusy => Status is ConversationStatus.Starting or ConversationStatus.Running;

    /// <summary>The latest run, or <see langword="null"/> before the first <c>RUN_STARTED</c>.</summary>
    public RunView? CurrentRun => Runs.IsEmpty ? null : Runs[^1];

    /// <summary>True while a reasoning span is open: the agent is thinking.</summary>
    public bool IsReasoning => !OpenReasoningSpans.IsEmpty;

    /// <summary>Interrupts still waiting for an answer.</summary>
    public IEnumerable<InterruptView> OpenInterrupts => Interrupts.Where(i => !i.IsAnswered);

    /// <summary>Finds a message by its AG-UI ID.</summary>
    public MessageView? FindMessage(string id) => Messages.Find(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>Finds a tool call by its AG-UI ID.</summary>
    public ToolCallView? FindToolCall(string id) => ToolCalls.Find(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    /// <summary>Finds an interrupt by its ID.</summary>
    public InterruptView? FindInterrupt(string id) => Interrupts.Find(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    /// <summary>The tool calls an assistant message carries, in order.</summary>
    public IEnumerable<ToolCallView> ToolCallsOf(AssistantMessageView message)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach (var id in message.ToolCallIds)
        {
            if (FindToolCall(id) is { } call)
            {
                yield return call;
            }
        }
    }
}

/// <summary>What a conversation is doing, as a user would describe it.</summary>
public enum ConversationStatus
{
    /// <summary>Ready for the next message.</summary>
    Idle,

    /// <summary>A turn was requested and the agent hasn't started a run yet.</summary>
    Starting,

    /// <summary>A run is streaming, or the client is running a tool between two runs of one turn.</summary>
    Running,

    /// <summary>The last run ended with interrupts that need an answer (<see cref="ConversationView.OpenInterrupts"/>).</summary>
    AwaitingInput,

    /// <summary>The agent reported the last run as cancelled: neither a success nor a failure.</summary>
    Cancelled,

    /// <summary>The user stopped the last turn. What arrived before the stop is kept.</summary>
    Stopped,

    /// <summary>The last turn failed; see <see cref="ConversationView.Failure"/>.</summary>
    Failed,
}

/// <summary>One AG-UI run, from <c>RUN_STARTED</c> to its terminal event.</summary>
/// <param name="RunId">The run's ID, from <c>RUN_STARTED</c>.</param>
public sealed record RunView(string RunId)
{
    /// <summary>The thread the producer said the run belongs to.</summary>
    public string? ThreadId { get; init; }

    /// <summary>The run this one branched from, when the producer reported one.</summary>
    public string? ParentRunId { get; init; }

    /// <summary>The protocol version the producer declared on <c>RUN_STARTED</c>, if any.</summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>How the run ended, or <see cref="RunOutcome.Running"/> while it streams.</summary>
    public RunOutcome Outcome { get; init; } = RunOutcome.Running;

    /// <summary>The run's <c>result</c>, when <c>RUN_FINISHED</c> carried one.</summary>
    public System.Text.Json.JsonElement? Result { get; init; }

    /// <summary>Token usage per provider and model, from the terminal event.</summary>
    public ImmutableList<TokenUsage> Usage { get; init; } = [];

    /// <summary>Tool calls the run left for the client (<c>pendingToolCallIds</c> on a success outcome).</summary>
    public ImmutableList<string> PendingToolCallIds { get; init; } = [];
}

/// <summary>How a run ended.</summary>
public enum RunOutcome
{
    /// <summary>The run hasn't ended.</summary>
    Running,

    /// <summary><c>RUN_FINISHED</c> with a success outcome, or none.</summary>
    Succeeded,

    /// <summary><c>RUN_FINISHED</c> with an interrupt outcome: the run stopped to ask.</summary>
    Interrupted,

    /// <summary><c>RUN_FINISHED</c> with a cancelled outcome: the agent stopped on purpose.</summary>
    Cancelled,

    /// <summary><c>RUN_ERROR</c>, or the stream broke before a terminal event.</summary>
    Failed,

    /// <summary>The client stopped reading: the user pressed Stop. No terminal event was received.</summary>
    Abandoned,
}

/// <summary>Why a turn failed.</summary>
/// <param name="Kind">The kind of failure, so a UI can choose its own words.</param>
/// <param name="Message">A description: the agent's own <c>RUN_ERROR</c> message, or the client's.</param>
public sealed record RunFailure(FailureKind Kind, string Message)
{
    /// <summary>The machine-readable <c>code</c> from <c>RUN_ERROR</c>, when the agent sent one.</summary>
    public string? Code { get; init; }

    /// <summary>
    /// The HTTP status the agent, or a gateway in front of it, answered with instead of an event stream:
    /// a 4xx for <see cref="FailureKind.Rejected"/>, a 5xx for <see cref="FailureKind.Unreachable"/>.
    /// </summary>
    public HttpStatusCode? StatusCode { get; init; }

    /// <summary>
    /// True when the failure shows that the run never started, so sending the same input again can't
    /// repeat any of its work: the connection was never made (refused, a name that didn't resolve, a
    /// connect timeout), or the request was refused with an HTTP status before any event. A 502 or 504
    /// doesn't count, because the gateway that sent it may have forwarded the request to a run that is
    /// executing. False means the client can't tell, which isn't the same as "it started".
    /// </summary>
    public bool RunNeverStarted { get; init; }
}

/// <summary>The kinds of failure a turn can end with.</summary>
public enum FailureKind
{
    /// <summary>The agent reported <c>RUN_ERROR</c>.</summary>
    AgentError,

    /// <summary>
    /// The agent couldn't be reached: no connection, a connection that failed with no usable response, or
    /// a 5xx status instead of a stream (see <see cref="RunFailure.StatusCode"/>).
    /// </summary>
    Unreachable,

    /// <summary>
    /// A timeout: the turn took longer than <see cref="ConversationOptions.TurnTimeout"/>, the connect timed
    /// out (<see cref="RunFailure.RunNeverStarted"/> is true) or the request timed out waiting for an agent
    /// that may be running it.
    /// </summary>
    TimedOut,

    /// <summary>
    /// The stream broke an AG-UI rule: one <c>AGUIChatClient</c> enforces, or a malformed value the reducer
    /// found (a malformed <c>STATE_DELTA</c> or <c>ACTIVITY_DELTA</c> patch, a first chunk without its ID or
    /// name, a chunk that contradicts its opener). The conversation cancels the turn when it happens.
    /// </summary>
    ProtocolViolation,

    /// <summary>
    /// A truncated run: the stream ended, or the connection broke mid-stream, without <c>RUN_FINISHED</c> or
    /// <c>RUN_ERROR</c>. The run may have done work on the server.
    /// </summary>
    StreamEnded,

    /// <summary>Anything else.</summary>
    Unexpected,

    /// <summary>
    /// The agent refused the request with a 4xx status instead of a stream (see <see cref="RunFailure.StatusCode"/>):
    /// invalid input, failed authorization, an answer to an interrupt it no longer holds open. The run never started.
    /// </summary>
    Rejected,
}

/// <summary>What one <c>MESSAGES_SNAPSHOT</c> did to the transcript.</summary>
/// <param name="Replaced">Messages the snapshot restated, replaced in place.</param>
/// <param name="Changed">Of those, messages whose content differed from the local copy.</param>
/// <param name="Added">Messages the client hadn't seen, appended in snapshot order.</param>
/// <param name="Dropped">Local messages absent from the snapshot, removed.</param>
/// <param name="Kept">Local activity or reasoning messages kept because the snapshot didn't cover their role.</param>
public sealed record ReconciliationSummary(int Replaced, int Changed, int Added, int Dropped, int Kept);
