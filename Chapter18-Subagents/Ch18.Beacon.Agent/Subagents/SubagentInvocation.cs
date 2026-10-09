using System.Security.Cryptography;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Ch18.Beacon.Agent.Subagents;

/// <summary>
/// One invocation of a subagent, as the AG-UI stream reports it: an ID that names this invocation (never
/// the subagent itself) and the events that open and close it. Each event travels as a content-less
/// <see cref="ChatResponseUpdate"/> whose <see cref="ChatResponseUpdate.RawRepresentation"/> is the event,
/// which <c>AGUI.Server</c> writes to the wire verbatim. What the subagent says and does in between is
/// <see cref="AttributedEventWriter"/>'s job.
/// </summary>
/// <remarks>
/// The protocol's rules for the bracket: announce an invocation before anything is attributed to it,
/// never announce an ID that is active or already finished in this run, and close every invocation
/// (finished, suspended or failed) before the run finishes. A suspended invocation may be continued in a
/// later run under the same ID, which is why the ID can be passed in.
/// </remarks>
public sealed class SubagentInvocation
{
    /// <summary>Starts describing an invocation of <paramref name="subagent"/>.</summary>
    /// <param name="subagent">The subagent's reusable name and description.</param>
    /// <param name="subagentRunId">The ID of a suspended invocation being continued; <see langword="null"/> mints a new one.</param>
    public SubagentInvocation(SubagentInfo subagent, string? subagentRunId = null)
    {
        ArgumentNullException.ThrowIfNull(subagent);
        Subagent = subagent;

        // Unique per invocation, opaque to clients. The name in front only makes logs readable.
        SubagentRunId = subagentRunId ?? $"{subagent.Name}-{RandomNumberGenerator.GetHexString(8, lowercase: true)}";
    }

    /// <summary>Which subagent this is an invocation of.</summary>
    public SubagentInfo Subagent { get; }

    /// <summary>The <c>subagentRunId</c> every event of this invocation carries.</summary>
    public string SubagentRunId { get; }

    /// <summary><c>SUBAGENT_STARTED</c>: the invocation begins, or a suspended one continues.</summary>
    /// <param name="parentSubagentRunId">The invocation that delegated this one, when subagents nest.</param>
    /// <param name="parentToolCallId">The tool call that spawned it, in the agents-as-tools pattern.</param>
    /// <param name="parentMessageId">The message that carried that tool call.</param>
    public ChatResponseUpdate Started(string? parentSubagentRunId = null, string? parentToolCallId = null, string? parentMessageId = null) =>
        Emit(new SubagentStartedEvent
        {
            SubagentRunId = SubagentRunId,
            Name = Subagent.Name,
            Description = Subagent.Description,
            ParentSubagentRunId = parentSubagentRunId,
            ParentToolCallId = parentToolCallId,
            ParentMessageId = parentMessageId,
        });

    /// <summary><c>SUBAGENT_FINISHED</c> with no outcome, which means success.</summary>
    /// <param name="result">An optional completion payload for clients.</param>
    public ChatResponseUpdate Finished(JsonElement? result = null) =>
        Emit(new SubagentFinishedEvent { SubagentRunId = SubagentRunId, Result = result });

    /// <summary>
    /// <c>SUBAGENT_FINISHED</c> with a suspended outcome: the invocation waits for outside input. It closes
    /// the invocation for this run only; a later run may continue it under the same ID.
    /// </summary>
    /// <param name="interruptIds">The run-level interrupts this invocation raised itself.</param>
    public ChatResponseUpdate Suspended(IEnumerable<string> interruptIds) =>
        Emit(new SubagentFinishedEvent
        {
            SubagentRunId = SubagentRunId,
            Outcome = new SubagentFinishedSuspendedOutcome { InterruptIds = [.. interruptIds] },
        });

    /// <summary><c>SUBAGENT_ERROR</c>: the invocation failed. It ends the invocation, not the run.</summary>
    /// <param name="message">A message written for the person reading the UI. No exception text.</param>
    /// <param name="code">A short machine-readable code.</param>
    public ChatResponseUpdate Failed(string message, string? code = null) =>
        Emit(new SubagentErrorEvent { SubagentRunId = SubagentRunId, Message = message, Code = code });

    /// <summary>Wraps an AG-UI event so it travels through a chat pipeline to <c>AGUI.Server</c>, which writes it as is.</summary>
    public static ChatResponseUpdate Emit(BaseEvent evt) => new() { RawRepresentation = evt };
}
