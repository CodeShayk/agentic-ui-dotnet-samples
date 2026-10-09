using System.Text.Json;
using AGUI.Abstractions;

namespace Ch14.Beacon.Agent.Approvals;

/// <summary>Where an approval is in its life. Only an <see cref="Open"/> approval can be answered, and only once.</summary>
public enum ApprovalStatus
{
    /// <summary>The model asked for a gated call in a run that hasn't finished yet.</summary>
    Proposed,

    /// <summary>The run ended with this approval's interrupt. A dispatcher can answer it until it expires.</summary>
    Open,

    /// <summary>Approved, as proposed or edited. The tool may spend it once, in the run that approved it.</summary>
    Approved,

    /// <summary>Declined by the dispatcher.</summary>
    Denied,

    /// <summary>Abandoned without a decision (a resume entry with the cancelled status).</summary>
    Cancelled,

    /// <summary>Proposed but never shown to anyone: the run failed, or the SDK left the call with the client.</summary>
    Withdrawn,

    /// <summary>The tool ran with it.</summary>
    Executed,
}

/// <summary>
/// One approval the server asked for, bound to the thread, the tool, the call and the exact arguments the
/// model proposed. The ID is the server's own and unguessable; it is the interrupt's ID on the wire.
/// Records are immutable: the store replaces one version with the next.
/// </summary>
public sealed record PendingApproval
{
    public required string Id { get; init; }

    public required string ThreadId { get; init; }

    /// <summary>The run that proposed the call.</summary>
    public required string RunId { get; init; }

    /// <summary>The client that sent that run.</summary>
    public required string ClientId { get; init; }

    public required string ToolCallId { get; init; }

    public required string ToolName { get; init; }

    /// <summary>The arguments exactly as the model proposed them.</summary>
    public required JsonElement Arguments { get; init; }

    /// <summary>What the call costs, priced by the server.</summary>
    public required decimal Amount { get; init; }

    /// <summary>One sentence for the dispatcher, written by the server.</summary>
    public required string Summary { get; init; }

    public required DateTimeOffset ProposedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public ApprovalStatus Status { get; init; } = ApprovalStatus.Proposed;

    /// <summary>The dispatcher's answer, once there is one.</summary>
    public ApprovalDecision? Decision { get; init; }

    /// <summary>The arguments the tool may run with: the dispatcher's edit if there was one, otherwise the proposal.</summary>
    public JsonElement ApprovedArguments => Decision?.EditedArguments ?? Arguments;
}

/// <summary>A dispatcher's answer, as the server accepted it.</summary>
/// <param name="Outcome"><see cref="ApprovalStatus.Approved"/>, <see cref="ApprovalStatus.Denied"/> or <see cref="ApprovalStatus.Cancelled"/>.</param>
/// <param name="ClientId">The client that sent the answer.</param>
/// <param name="RunId">The resuming run that carried it. An approval can be spent only in this run.</param>
/// <param name="At">When the server accepted it.</param>
/// <param name="Reason">The dispatcher's reason, if one was given.</param>
/// <param name="EditedArguments">The full replacement arguments, if the dispatcher changed the call.</param>
/// <param name="EditedAmount">What the edited call costs, re-priced by the server.</param>
public sealed record ApprovalDecision(
    ApprovalStatus Outcome,
    string ClientId,
    string RunId,
    DateTimeOffset At,
    string? Reason = null,
    JsonElement? EditedArguments = null,
    decimal? EditedAmount = null)
{
    /// <summary>The resume entry's status, as the client sent it.</summary>
    public string? EntryStatus { get; init; }

    /// <summary>The resume entry's payload, as the client sent it.</summary>
    public JsonElement? EntryPayload { get; init; }

    /// <summary>True once the run that carried this answer has ended, however it ended.</summary>
    public bool RunEnded { get; init; }

    /// <summary>For an answer sent again: the earlier decision this one takes over.</summary>
    public ApprovalDecision? Replaces { get; init; }

    /// <summary>
    /// Whether <paramref name="entry"/> repeats the answer exactly: the same status and the same payload. The
    /// concepts page asks that such a resume be safe to replay; metadata is envelope data and doesn't count.
    /// </summary>
    public bool Repeats(AGUIResume entry) =>
        entry.Status == EntryStatus && (entry.Payload, EntryPayload) switch
        {
            (null, null) => true,
            ({ } sent, { } first) => JsonElement.DeepEquals(sent, first),
            _ => false,
        };
}
