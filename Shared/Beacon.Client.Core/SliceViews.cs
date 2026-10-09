using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Beacon.Client.Core;

/// <summary>One tool call: what the agent asked for, how far it got and what came back.</summary>
/// <param name="Id">The <c>toolCallId</c>.</param>
/// <param name="Name">The tool's name; empty when a result arrived for a call this client never saw.</param>
public sealed record ToolCallView(string Id, string Name)
{
    /// <summary>The assistant message that carries the call (<c>parentMessageId</c>, or the call's own ID).</summary>
    public string? ParentMessageId { get; init; }

    /// <summary>The run that opened the call.</summary>
    public string? RunId { get; init; }

    /// <summary>The subagent invocation the call belongs to; <see langword="null"/> for the parent agent.</summary>
    public string? SubagentRunId { get; init; }

    /// <summary>Where the call is in its life.</summary>
    public ToolCallStatus Status { get; init; } = ToolCallStatus.Streaming;

    /// <summary>Who runs the tool: the agent's server, or this client (a frontend tool).</summary>
    public ToolExecutor Executor { get; init; }

    /// <summary>The argument text so far: the concatenated <c>TOOL_CALL_ARGS</c> deltas. Untrusted, possibly partial.</summary>
    public string ArgumentsText { get; init; } = string.Empty;

    /// <summary>The parsed arguments once they are complete and valid JSON; otherwise <see langword="null"/>.</summary>
    public JsonElement? Arguments { get; init; }

    /// <summary>The result text, once one arrived. Untrusted.</summary>
    public string? Result { get; init; }

    /// <summary>The error a client-side tool threw, if it failed.</summary>
    public string? Error { get; init; }

    /// <summary>An opaque value the producer attached to the call (<c>REASONING_ENCRYPTED_VALUE</c>, subtype <c>tool-call</c>).</summary>
    public string? EncryptedValue { get; init; }

    /// <summary>True once the call can't change any more on its own: it completed or was abandoned.</summary>
    public bool IsFinished => Status is ToolCallStatus.Completed or ToolCallStatus.Abandoned;
}

/// <summary>Where a tool call is in its life.</summary>
public enum ToolCallStatus
{
    /// <summary><c>TOOL_CALL_START</c> arrived; argument deltas are still streaming. Seen only with a <see cref="WireEventTap"/>.</summary>
    Streaming,

    /// <summary>The arguments are complete; the tool is running and no result has arrived.</summary>
    InProgress,

    /// <summary>A result arrived.</summary>
    Completed,

    /// <summary>The run ended with an approval interrupt for this call; it waits for a person's decision.</summary>
    AwaitingApproval,

    /// <summary>The run ended without a result: the call was left for the client to answer (a frontend tool).</summary>
    AwaitingClient,

    /// <summary>The turn was stopped or failed before the call finished.</summary>
    Abandoned,
}

/// <summary>Who executes a tool call.</summary>
public enum ToolExecutor
{
    /// <summary>Not known yet (for example, only the wire events have been seen).</summary>
    Unknown,

    /// <summary>The agent's server runs it (a backend tool); <c>AGUIChatClient</c> marks it informational.</summary>
    Agent,

    /// <summary>This client runs it (a frontend tool declared in <see cref="ConversationOptions.Tools"/>).</summary>
    Client,
}

/// <summary>The state the agent and the client share, kept as a JSON document.</summary>
public sealed record SharedStateView
{
    /// <summary>No state yet.</summary>
    public static SharedStateView Empty { get; } = new();

    /// <summary>
    /// The current document. Read-only: change it through <see cref="AgentConversation.UpdateState"/>.
    /// <see langword="null"/> before any state arrived, or when the state is JSON <c>null</c>.
    /// </summary>
    public JsonNode? Document { get; init; }

    /// <summary>True once a snapshot, a delta or a local edit has set the state.</summary>
    public bool HasValue { get; init; }

    /// <summary>Increments on every change, so a component can tell cheaply that the document changed.</summary>
    public int Version { get; init; }

    /// <summary>Who made the latest change.</summary>
    public StateChangeSource LastChangedBy { get; init; }

    /// <summary>
    /// True after a <c>STATE_DELTA</c> failed to apply. The document kept its prior value (patches are
    /// atomic), but it may now differ from the agent's; the next <c>STATE_SNAPSHOT</c> clears the flag.
    /// </summary>
    public bool NeedsResync { get; init; }

    /// <summary>Why the last failed delta didn't apply.</summary>
    public string? LastPatchError { get; init; }

    /// <summary>The document as a <see cref="JsonElement"/>, for <c>RunAgentInput.state</c>; <see langword="null"/> when there is none.</summary>
    public JsonElement? ToJsonElement()
    {
        if (!HasValue || Document is null)
        {
            return null;
        }

        // Write and re-read rather than serialize: no reflection, so it's safe under trimming and AOT.
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Document.WriteTo(writer);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}

/// <summary>Who changed the shared state last.</summary>
public enum StateChangeSource
{
    /// <summary>Nobody yet.</summary>
    None,

    /// <summary>The agent, with <c>STATE_SNAPSHOT</c> or <c>STATE_DELTA</c>.</summary>
    Agent,

    /// <summary>This client, with <see cref="AgentConversation.UpdateState"/>.</summary>
    Client,
}

/// <summary>A named phase of a run (<c>STEP_STARTED</c> to <c>STEP_FINISHED</c>).</summary>
/// <param name="Name">The step's name; unique per owner while it's active.</param>
public sealed record StepView(string Name)
{
    /// <summary>The subagent that owns the step; <see langword="null"/> for the parent agent.</summary>
    public string? SubagentRunId { get; init; }

    /// <summary>The run the step belongs to.</summary>
    public string? RunId { get; init; }

    /// <summary>True until <c>STEP_FINISHED</c> arrives.</summary>
    public bool IsActive { get; init; } = true;
}

/// <summary>One subagent invocation (<c>SUBAGENT_STARTED</c> to its terminal event).</summary>
/// <param name="SubagentRunId">The invocation's ID, which attributes events to it.</param>
/// <param name="Name">The subagent's reusable name, such as <c>parts-specialist</c>.</param>
public sealed record SubagentView(string SubagentRunId, string Name)
{
    /// <summary>What the subagent does, if the producer said.</summary>
    public string? Description { get; init; }

    /// <summary>The invocation that started this one, for nesting.</summary>
    public string? ParentSubagentRunId { get; init; }

    /// <summary>The tool call that spawned the invocation, if any.</summary>
    public string? ParentToolCallId { get; init; }

    /// <summary>The message that spawned the invocation, if any.</summary>
    public string? ParentMessageId { get; init; }

    /// <summary>The run the invocation started in.</summary>
    public string? RunId { get; init; }

    /// <summary>Where the invocation is in its life.</summary>
    public SubagentStatus Status { get; init; } = SubagentStatus.Running;

    /// <summary>The <c>result</c> from <c>SUBAGENT_FINISHED</c>, if any.</summary>
    public JsonElement? Result { get; init; }

    /// <summary>The message from <c>SUBAGENT_ERROR</c>.</summary>
    public string? Error { get; init; }

    /// <summary>The code from <c>SUBAGENT_ERROR</c>.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The interrupts a suspended invocation waits on.</summary>
    public ImmutableList<string> InterruptIds { get; init; } = [];
}

/// <summary>Where a subagent invocation is in its life.</summary>
public enum SubagentStatus
{
    /// <summary>Started and not finished.</summary>
    Running,

    /// <summary>Finished with a success outcome.</summary>
    Succeeded,

    /// <summary>Finished with a suspended outcome: it waits for outside input.</summary>
    Suspended,

    /// <summary>Ended with <c>SUBAGENT_ERROR</c>. The parent run carries on.</summary>
    Failed,

    /// <summary>
    /// Was suspended, and the agent accepted the client's answers to the interrupts it waited on. A later
    /// <c>SUBAGENT_STARTED</c> with the same ID continues it (<see cref="Running"/>); a producer that
    /// continues the work under a new ID leaves this one here, no longer waiting.
    /// </summary>
    Resumed,
}

/// <summary>A pause the agent asked for: an approval or a request for input (<c>RUN_FINISHED</c> with an interrupt outcome).</summary>
/// <param name="Id">The interrupt's ID; a resume answers it by this ID.</param>
/// <param name="Kind">An approval of a tool call, or a request for input.</param>
public sealed record InterruptView(string Id, InterruptKind Kind)
{
    /// <summary>The producer's reason, such as <c>tool_call</c> or <c>input_required</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>The prompt for whoever answers.</summary>
    public string? Message { get; init; }

    /// <summary>The tool call the interrupt concerns, if any.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>A JSON Schema the answer must satisfy, if the producer sent one.</summary>
    public JsonElement? ResponseSchema { get; init; }

    /// <summary>When the interrupt expires, as the producer wrote it.</summary>
    public string? ExpiresAt { get; init; }

    /// <summary>The interrupt's metadata.</summary>
    public JsonElement? Metadata { get; init; }

    /// <summary>The run that raised it.</summary>
    public string? RunId { get; init; }

    /// <summary>The subagent that raised it, if any.</summary>
    public string? SubagentRunId { get; init; }

    /// <summary>
    /// The request as <c>AGUIChatClient</c> surfaced it: a <see cref="ToolApprovalRequestContent"/> or an
    /// <c>InterruptRequestContent</c>. <see cref="AgentConversation.RespondAsync"/> sends it back with the answer.
    /// </summary>
    public InputRequestContent? Request { get; init; }

    /// <summary>
    /// True once the agent accepted an answer to it, or its abandonment: the resuming run started.
    /// An answer the agent refused leaves it open (see <see cref="LastAnswerFailure"/>).
    /// </summary>
    public bool IsAnswered { get; init; }

    /// <summary>
    /// Why the last attempt to answer it didn't reach a run: the agent refused it (a 4xx, or a stream that
    /// opened with <c>RUN_ERROR</c>), or the request failed before the resuming run started. The interrupt
    /// is still open; answer it again or abandon it. <see langword="null"/> when no attempt failed.
    /// </summary>
    public RunFailure? LastAnswerFailure { get; init; }
}

/// <summary>The two kinds of interrupt a client answers differently.</summary>
public enum InterruptKind
{
    /// <summary>Approve or deny a tool call (<c>ToolApprovalResponseContent</c>).</summary>
    ToolApproval,

    /// <summary>Supply input, usually shaped by a response schema (<c>InterruptResponseContent</c>).</summary>
    Input,
}
