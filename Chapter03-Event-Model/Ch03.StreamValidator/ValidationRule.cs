using static Ch03.StreamValidator.DiagnosticSeverity;

namespace Ch03.StreamValidator;

/// <summary>A rule from the AG-UI 1.0 specification that the validator checks.</summary>
/// <param name="Id">A stable identifier, such as <c>TXT002</c>.</param>
/// <param name="Severity">Whether a conforming consumer must reject the stream or survives it.</param>
/// <param name="Title">The rule in one line.</param>
/// <param name="Spec">The specification page the rule comes from, relative to https://docs.ag-ui.com.</param>
public sealed record ValidationRule(string Id, DiagnosticSeverity Severity, string Title, string Spec)
{
    public Uri SpecUri => new($"https://docs.ag-ui.com{Spec}");
}

/// <summary>Every rule the validator checks, grouped by event family.</summary>
public static class Rules
{
    private const string EventModel = "/spec/1.0/basic";
    private const string Processing = "/spec/1.0/basic/processing";
    private const string Lifecycle = "/spec/1.0/events/lifecycle";
    private const string RunInput = "/spec/1.0/basic/run-input";
    private const string Versioning = "/spec/1.0/basic/versioning";
    private const string Transports = "/spec/1.0/basic/transports";
    private const string Streaming = "/spec/1.0/basic/patterns/streaming";
    private const string Interrupts = "/spec/1.0/basic/patterns/interrupt-resume";
    private const string TextMessages = "/spec/1.0/events/text-messages";
    private const string ToolCalls = "/spec/1.0/events/tool-calls";
    private const string Reasoning = "/spec/1.0/events/reasoning";
    private const string State = "/spec/1.0/events/state";
    private const string Activity = "/spec/1.0/events/activity";
    private const string Subagents = "/spec/1.0/events/subagents";
    private const string Passthrough = "/spec/1.0/events/passthrough";

    // The envelope and the processing model.
    public static ValidationRule NotAnEvent { get; } = new("ENV001", Error, "Every event is a JSON object with a string type", EventModel);
    public static ValidationRule UnknownEventType { get; } = new("ENV002", Warning, "Event type the protocol does not describe (consumers drop it)", Processing);
    public static ValidationRule MissingField { get; } = new("ENV003", Error, "Required field is missing", Processing);
    public static ValidationRule MalformedField { get; } = new("ENV004", Error, "Known field carries a value the schema rejects", Processing);
    public static ValidationRule NullForAbsent { get; } = new("ENV005", Error, "Optional field sent as null; absent means absent", EventModel);
    public static ValidationRule UnknownProperty { get; } = new("ENV006", Warning, "Property the protocol does not describe (consumers strip it)", Processing);
    public static ValidationRule UnknownMember { get; } = new("ENV007", Warning, "Unrecognized enum value or union member (consumers strip it)", Processing);

    // Runs.
    public static ValidationRule FirstEvent { get; } = new("RUN001", Error, "A stream begins with RUN_STARTED or RUN_ERROR", Lifecycle);
    public static ValidationRule NestedRun { get; } = new("RUN002", Error, "RUN_STARTED while a run is still active", Lifecycle);
    public static ValidationRule AfterClose { get; } = new("RUN003", Error, "Event after the run closed", Lifecycle);
    public static ValidationRule Truncated { get; } = new("RUN004", Error, "Stream ended without a terminal event (truncated run)", Transports);
    public static ValidationRule BoundaryMismatch { get; } = new("RUN005", Error, "RUN_FINISHED identifies a different run than RUN_STARTED", RunInput);
    public static ValidationRule ThreadChanged { get; } = new("RUN006", Error, "Runs on one stream carry different threadIds", RunInput);
    public static ValidationRule RunIdReused { get; } = new("RUN007", Error, "runId reused for another run on the same thread", EventModel);
    public static ValidationRule DuplicateInterruptId { get; } = new("RUN008", Error, "Interrupt ids are not unique within the run", Interrupts);
    public static ValidationRule InterruptsOnOtherOutcome { get; } = new("RUN009", Error, "A success or cancelled outcome carries interrupts", Interrupts);
    public static ValidationRule CancelledWithResult { get; } = new("RUN010", Warning, "A cancelled run carries a result", Lifecycle);
    public static ValidationRule WrongThread { get; } = new("RUN011", Error, "The stream answers a different thread than the request", RunInput);
    public static ValidationRule RequestedRunNotLast { get; } = new("RUN012", Error, "The requested run is not the last run on the stream", RunInput);

    // Versioning and usage.
    public static ValidationRule NoVersion { get; } = new("VER001", Warning, "RUN_STARTED does not declare protocolVersion", Versioning);
    public static ValidationRule OtherVersion { get; } = new("VER002", Warning, "RUN_STARTED declares a version other than 1.0", Versioning);
    public static ValidationRule TotalMismatch { get; } = new("USG001", Warning, "totalTokens is not inputTokens plus outputTokens", Lifecycle);
    public static ValidationRule PartExceedsTotal { get; } = new("USG002", Warning, "A token count part exceeds its total", Lifecycle);

    // Text messages.
    public static ValidationRule TextAlreadyOpen { get; } = new("TXT001", Error, "TEXT_MESSAGE_START for a message that is already open", TextMessages);
    public static ValidationRule TextNotOpen { get; } = new("TXT002", Error, "TEXT_MESSAGE_CONTENT or TEXT_MESSAGE_END for a message that is not open", TextMessages);
    public static ValidationRule TextLeftOpen { get; } = new("TXT003", Error, "Text message still open when the run ends", TextMessages);
    public static ValidationRule TextReopenDisagrees { get; } = new("TXT004", Warning, "Reopening TEXT_MESSAGE_START disagrees with the message", TextMessages);

    // Tool calls.
    public static ValidationRule ToolAlreadyOpen { get; } = new("TOOL001", Error, "TOOL_CALL_START for a call that is already open", ToolCalls);
    public static ValidationRule ToolNotOpen { get; } = new("TOOL002", Error, "TOOL_CALL_ARGS or TOOL_CALL_END for a call that is not open", ToolCalls);
    public static ValidationRule ToolLeftOpen { get; } = new("TOOL003", Error, "Tool call still open when the run ends", ToolCalls);
    public static ValidationRule ToolReopenDisagrees { get; } = new("TOOL004", Warning, "Reopening TOOL_CALL_START disagrees with the call", ToolCalls);
    public static ValidationRule ResultBeforeEnd { get; } = new("TOOL005", Warning, "TOOL_CALL_RESULT arrived before the call closed", ToolCalls);
    public static ValidationRule PendingMismatch { get; } = new("TOOL006", Error, "pendingToolCallIds does not name exactly the unanswered calls", ToolCalls);
    public static ValidationRule FrontendToolAnswered { get; } = new("TOOL007", Error, "The producer answered a frontend tool call", ToolCalls);

    // Reasoning.
    public static ValidationRule ReasoningAlreadyOpen { get; } = new("RSN001", Error, "REASONING_MESSAGE_START for a message that is already open", Reasoning);
    public static ValidationRule ReasoningNotOpen { get; } = new("RSN002", Error, "REASONING_MESSAGE_CONTENT or REASONING_MESSAGE_END for a message that is not open", Reasoning);
    public static ValidationRule ReasoningLeftOpen { get; } = new("RSN003", Error, "Reasoning message still open when the run ends", Reasoning);
    public static ValidationRule SpanAlreadyOpen { get; } = new("RSN004", Error, "REASONING_START for a span that is already open", Reasoning);
    public static ValidationRule SpanNotOpen { get; } = new("RSN005", Error, "REASONING_END for a span that is not open", Reasoning);
    public static ValidationRule SpanLeftOpen { get; } = new("RSN006", Error, "Reasoning span still open when the run ends", Reasoning);
    public static ValidationRule UnknownEntity { get; } = new("RSN007", Warning, "REASONING_ENCRYPTED_VALUE names nothing the stream created", Reasoning);

    // The chunked form.
    public static ValidationRule FirstChunkIncomplete { get; } = new("CHK001", Error, "First chunk lacks the fields that open an item", Streaming);
    public static ValidationRule ChunkContradicts { get; } = new("CHK002", Error, "Continuation chunk contradicts its opener", Streaming);
    public static ValidationRule ChunkAmbiguous { get; } = new("CHK003", Error, "Untagged continuation chunk has no unique stream to continue", Streaming);
    public static ValidationRule FormsMixed { get; } = new("CHK004", Error, "Explicit event names an item a chunk stream is assembling", Streaming);

    // Steps.
    public static ValidationRule StepAlreadyOpen { get; } = new("STEP001", Error, "STEP_STARTED for a step that is already open", Lifecycle);
    public static ValidationRule StepNotOpen { get; } = new("STEP002", Error, "STEP_FINISHED for a step that is not open", Lifecycle);
    public static ValidationRule StepLeftOpen { get; } = new("STEP003", Error, "Step still open when the run ends", Lifecycle);

    // Messages, state and activity.
    public static ValidationRule MessageIdCollision { get; } = new("MSG001", Error, "messageId already identifies a different message", EventModel);
    public static ValidationRule SnapshotDuplicateId { get; } = new("MSG002", Error, "MESSAGES_SNAPSHOT repeats a message id", State);
    public static ValidationRule ActivityWithoutBaseline { get; } = new("ACT001", Warning, "ACTIVITY_DELTA has no activity message to amend", Activity);
    public static ValidationRule ActivityOwnerDisagrees { get; } = new("ACT002", Warning, "replace:false snapshot disagrees with the activity's owner", Activity);

    // Subagents.
    public static ValidationRule SubagentAlreadyActive { get; } = new("SUB001", Error, "SUBAGENT_STARTED for an invocation that is already active", Subagents);
    public static ValidationRule SubagentIdReused { get; } = new("SUB002", Error, "subagentRunId reused for a second invocation in one run", Subagents);
    public static ValidationRule SubagentNotActive { get; } = new("SUB003", Error, "SUBAGENT_FINISHED or SUBAGENT_ERROR for an invocation that is not active", Subagents);
    public static ValidationRule SubagentLeftActive { get; } = new("SUB004", Error, "Subagent invocation still active when the run finishes", Subagents);
    public static ValidationRule OwnerMismatch { get; } = new("SUB005", Error, "Continuation attributed to a different owner than its opener", Subagents);
    public static ValidationRule AnnouncedLate { get; } = new("SUB006", Error, "Invocation announced after events were attributed to it", Subagents);
    public static ValidationRule AttributedAfterFinish { get; } = new("SUB007", Warning, "Event attributed to an invocation that already finished", Subagents);
    public static ValidationRule UnknownParentInvocation { get; } = new("SUB008", Error, "parentSubagentRunId names an invocation not announced in this run", Subagents);
    public static ValidationRule ToolOwnerMismatch { get; } = new("SUB009", Error, "Tool call attributed differently from the message that holds it", Subagents);
    public static ValidationRule SuccessWithInterruptIds { get; } = new("SUB010", Error, "A subagent's success outcome carries interrupt ids", Subagents);

    // Passthrough.
    public static ValidationRule CustomNameUnprefixed { get; } = new("PASS001", Warning, "CUSTOM name has no vendor or application prefix", Passthrough);

    /// <summary>The whole catalog, in the order above.</summary>
    public static IReadOnlyList<ValidationRule> All { get; } =
    [
        NotAnEvent, UnknownEventType, MissingField, MalformedField, NullForAbsent, UnknownProperty, UnknownMember,
        FirstEvent, NestedRun, AfterClose, Truncated, BoundaryMismatch, ThreadChanged, RunIdReused,
        DuplicateInterruptId, InterruptsOnOtherOutcome, CancelledWithResult, WrongThread, RequestedRunNotLast,
        NoVersion, OtherVersion, TotalMismatch, PartExceedsTotal,
        TextAlreadyOpen, TextNotOpen, TextLeftOpen, TextReopenDisagrees,
        ToolAlreadyOpen, ToolNotOpen, ToolLeftOpen, ToolReopenDisagrees, ResultBeforeEnd, PendingMismatch, FrontendToolAnswered,
        ReasoningAlreadyOpen, ReasoningNotOpen, ReasoningLeftOpen, SpanAlreadyOpen, SpanNotOpen, SpanLeftOpen, UnknownEntity,
        FirstChunkIncomplete, ChunkContradicts, ChunkAmbiguous, FormsMixed,
        StepAlreadyOpen, StepNotOpen, StepLeftOpen,
        MessageIdCollision, SnapshotDuplicateId, ActivityWithoutBaseline, ActivityOwnerDisagrees,
        SubagentAlreadyActive, SubagentIdReused, SubagentNotActive, SubagentLeftActive, OwnerMismatch,
        AnnouncedLate, AttributedAfterFinish, UnknownParentInvocation, ToolOwnerMismatch, SuccessWithInterruptIds,
        CustomNameUnprefixed,
    ];
}
