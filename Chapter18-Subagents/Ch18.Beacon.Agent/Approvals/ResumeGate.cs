// Copied from Chapter 14 (Ch14.Beacon.Agent/Approvals); each chapter's code is a self-contained checkpoint.
using System.Globalization;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.Options;

namespace Ch18.Beacon.Agent.Approvals;

/// <summary>Why a run was refused before it started, as an HTTP problem.</summary>
public sealed record RunRejection(int StatusCode, string Title, string Detail);

/// <summary>
/// Checks a run's resume entries against the approvals this server recorded, before the run starts.
/// An entry counts only if it answers an open approval on this thread, in time, about exactly the call
/// the server proposed, with an edit (if any) the policy accepts. Every open approval on the thread
/// must be answered. Then the decisions are applied all at once, so each approval is answered once.
/// </summary>
public sealed class ResumeGate(IApprovalStore approvals, SpendingApprovalPolicy policy, IOptions<ApprovalOptions> options, TimeProvider time)
{
    public (IReadOnlyList<PendingApproval> Decided, RunRejection? Rejection) Check(RunAgentInput input, ApprovalRun run)
    {
        var now = time.GetUtcNow();
        List<(string ApprovalId, ApprovalDecision Decision)> decisions = [];

        foreach (var entry in input.Resume ?? [])
        {
            if (decisions.Exists(d => d.ApprovalId == entry.InterruptId))
            {
                return Refuse(400, "Duplicate resume entry", $"Interrupt {entry.InterruptId} is answered twice.");
            }

            // Forged, replayed, from another thread, never shown or already answered: all look the same from here.
            if (approvals.Find(input.ThreadId, entry.InterruptId) is not { Status: ApprovalStatus.Open } open)
            {
                return Refuse(409, "Interrupt not open", $"Interrupt {entry.InterruptId} is not open on this thread.");
            }

            // Abandoning is always allowed, even after the approval expired: it's how a thread moves on.
            if (entry.Status == ResumeStatus.Cancelled)
            {
                decisions.Add((open.Id, new ApprovalDecision(ApprovalStatus.Cancelled, run.ClientId, run.RunId, now)));
                continue;
            }

            if (entry.Status != ResumeStatus.Resolved)
            {
                return Refuse(400, "Invalid resume entry", $"The status of interrupt {open.Id} must be resolved or cancelled.");
            }

            if (open.ExpiresAt <= now)
            {
                return Refuse(409, "Interrupt expired",
                    $"Interrupt {open.Id} expired at {open.ExpiresAt:O}; it can only be cancelled.");
            }

            if (!ApprovalPayload.TryRead(entry.Payload, options.Value.MaxReasonLength, out var answer, out var problem))
            {
                return Refuse(400, "Invalid approval", problem);
            }

            // A client may echo the call it saw (AGUIChatClient always does). The echo must be the proposal.
            if (answer.ToolCall is { } echoed && !SameCall(open, echoed))
            {
                return Refuse(409, "Approval does not match", $"Interrupt {open.Id} approves a different call than the one proposed.");
            }

            if (!answer.Approved)
            {
                decisions.Add((open.Id, new ApprovalDecision(ApprovalStatus.Denied, run.ClientId, run.RunId, now, answer.Reason)));
                continue;
            }

            // An edit is checked like a new proposal: only the editable arguments may change, and the
            // policy must accept the result.
            if (answer.EditedArguments is { } edited && !ToolArguments.Same(edited, open.Arguments))
            {
                if (CheckEdit(open, edited, out var amount) is { } editProblem)
                {
                    return Refuse(400, "Invalid edit", editProblem);
                }

                decisions.Add((open.Id, new ApprovalDecision(ApprovalStatus.Approved, run.ClientId, run.RunId, now, answer.Reason, edited, amount)));
                continue;
            }

            decisions.Add((open.Id, new ApprovalDecision(ApprovalStatus.Approved, run.ClientId, run.RunId, now, answer.Reason)));
        }

        // Omission is not abandonment: every open approval on the thread needs an answer first.
        if (approvals.OpenOn(input.ThreadId).FirstOrDefault(a => !decisions.Exists(d => d.ApprovalId == a.Id)) is { } uncovered)
        {
            return Refuse(409, "Interrupt still open", $"Interrupt {uncovered.Id} is waiting for an answer. Resolve or cancel it first.");
        }

        // All or nothing, and only once: a second copy of this request, even a concurrent one, finds nothing open.
        if (!approvals.TryDecide(input.ThreadId, decisions, out var decided))
        {
            return Refuse(409, "Interrupt not open", "Another request answered this interrupt first.");
        }

        return (decided, null);

        static (IReadOnlyList<PendingApproval>, RunRejection?) Refuse(int status, string title, string detail) =>
            ([], new RunRejection(status, title, detail));
    }

    // An edit is a full replacement of the arguments. Only the editable ones may differ from the proposal,
    // and the result must be a call the policy accepts. Its cost is the server's price, not the client's.
    private string? CheckEdit(PendingApproval open, JsonElement edited, out decimal amount)
    {
        amount = 0m;
        if (edited.ValueKind != JsonValueKind.Object)
        {
            return "editedArgs must be an object holding every argument of the call.";
        }

        var editable = SpendingApprovalPolicy.EditableArguments[open.ToolName];
        var proposed = open.Arguments.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        var names = edited.EnumerateObject().Select(p => p.Name).ToList();
        if (names.Count != proposed.Count || names.Any(n => !proposed.ContainsKey(n)))
        {
            return $"editedArgs must hold exactly these arguments: {string.Join(", ", proposed.Keys)}.";
        }

        foreach (var argument in edited.EnumerateObject())
        {
            if (!editable.Contains(argument.Name) && !JsonElement.DeepEquals(argument.Value, proposed[argument.Name]))
            {
                return $"{argument.Name} cannot be changed when approving; only {string.Join(" and ", editable)} can.";
            }
        }

        var assessment = policy.Assess(open.ToolName, edited);
        if (assessment.Need == ApprovalNeed.Invalid)
        {
            return assessment.Summary;
        }

        amount = assessment.Amount;
        return null;
    }

    private static bool SameCall(PendingApproval open, JsonElement echoed) =>
        echoed.ValueKind == JsonValueKind.Object
        && echoed.TryGetProperty("callId", out var callId) && callId.ValueKind == JsonValueKind.String && callId.GetString() == open.ToolCallId
        && echoed.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == open.ToolName
        && (!echoed.TryGetProperty("arguments", out var arguments) || ToolArguments.Same(arguments, open.Arguments));
}

/// <summary>
/// An approval answer's payload: <c>approved</c> (required), and optionally <c>reason</c>, <c>editedArgs</c>
/// (Beacon's response schema) and <c>toolCall</c> (the echo <c>AGUIChatClient</c> adds).
/// </summary>
internal sealed record ApprovalPayload(bool Approved, string? Reason, JsonElement? ToolCall, JsonElement? EditedArguments)
{
    public static bool TryRead(JsonElement? payload, int maxReasonLength, out ApprovalPayload answer, out string problem)
    {
        answer = new ApprovalPayload(false, null, null, null);
        problem = string.Empty;

        if (payload is not { ValueKind: JsonValueKind.Object } body
            || !body.TryGetProperty("approved", out var approved)
            || approved.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            problem = "An approval payload must be an object with a boolean approved property.";
            return false;
        }

        string? reason = null;
        if (body.TryGetProperty("reason", out var reasonValue))
        {
            if (reasonValue.ValueKind != JsonValueKind.String || reasonValue.GetString()!.Length > maxReasonLength)
            {
                problem = string.Create(CultureInfo.InvariantCulture, $"reason must be a string of at most {maxReasonLength} characters.");
                return false;
            }

            reason = reasonValue.GetString()!.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        }

        JsonElement? toolCall = body.TryGetProperty("toolCall", out var call) && call.ValueKind == JsonValueKind.Object ? call.Clone() : null;
        JsonElement? edited = body.TryGetProperty("editedArgs", out var args) ? args.Clone() : null;

        if (edited is not null && !approved.GetBoolean())
        {
            problem = "Only an approval can carry editedArgs.";
            return false;
        }

        answer = new ApprovalPayload(approved.GetBoolean(), reason, toolCall, edited);
        return true;
    }
}
