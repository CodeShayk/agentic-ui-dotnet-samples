// Adapted from Chapter 14 (Ch14.Beacon.Agent/Approvals/ApprovalAudit.cs). Chapter 23 adds the
// authenticated user to every entry and classifies the fields that identify people or carry their words.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Ch27.Beacon.Agent.Security.Logging;
using Microsoft.Extensions.Logging;

namespace Ch27.Beacon.Agent.Security.Approvals;

/// <summary>What happened to an approval, or to a consequential call.</summary>
public enum ApprovalEvent
{
    Requested,
    Approved,
    Edited,
    Denied,
    Cancelled,
    Replayed,
    Withdrawn,
    Executed,
    Blocked,
    Refused,
}

/// <summary>
/// One line of the approval audit: what happened, when, on which thread and run, for which authenticated
/// user, from which client, and to which call.
/// </summary>
public sealed record ApprovalAuditEntry(
    DateTimeOffset At,
    ApprovalEvent Event,
    string ThreadId,
    string RunId,
    string UserId,
    string ClientId,
    string? ApprovalId = null,
    string? ToolName = null,
    string? ToolCallId = null,
    string? Arguments = null,
    decimal? Amount = null,
    string? Reason = null,
    string? Result = null)
{
    /// <summary>An entry about an approval, with its call as the server recorded it.</summary>
    public static ApprovalAuditEntry For(ApprovalEvent kind, PendingApproval approval, DateTimeOffset at, ApprovalRun run) => new(
        at,
        kind,
        approval.ThreadId,
        run.RunId,
        run.UserId,
        run.ClientId,
        approval.Id,
        approval.ToolName,
        approval.ToolCallId,
        approval.ApprovedArguments.GetRawText(),
        approval.Decision?.EditedAmount ?? approval.Amount,
        approval.Decision?.Reason);
}

/// <summary>Where approval decisions are recorded.</summary>
public interface IApprovalAudit
{
    void Record(ApprovalAuditEntry entry);

    IReadOnlyList<ApprovalAuditEntry> Entries { get; }
}

/// <summary>
/// Keeps the audit in memory and writes each entry to the log. The entry keeps every value; the log line
/// hashes the user and erases the arguments and the reason. A production audit belongs in an append-only
/// store with its own access control.
/// </summary>
public sealed partial class LoggingApprovalAudit(ILogger<LoggingApprovalAudit> logger) : IApprovalAudit
{
    private readonly ConcurrentQueue<ApprovalAuditEntry> _entries = new();

    public IReadOnlyList<ApprovalAuditEntry> Entries => [.. _entries];

    public void Record(ApprovalAuditEntry entry)
    {
        _entries.Enqueue(entry);
        Log(
            logger,
            entry.Event,
            entry.ApprovalId ?? "-",
            entry.ToolName ?? "-",
            entry.Arguments ?? "{}",
            entry.Amount is { } amount ? SpendingApprovalPolicy.Money(amount) : "-",
            entry.ThreadId,
            entry.RunId,
            entry.UserId,
            entry.ClientId,
            entry.Reason ?? "-",
            entry.Result ?? "-");
    }

    [LoggerMessage(2310, LogLevel.Information,
        "{Event} {ApprovalId} {ToolName} '{Arguments}' {Amount} | thread {ThreadId} run {RunId} user {UserId} client {ClientId} | reason: '{Reason}' | result: {Result}")]
    private static partial void Log(
        ILogger logger, ApprovalEvent @event, string approvalId, string toolName, [ConversationContent] string arguments, string amount,
        string threadId, string runId, [PersonalData] string userId, string clientId, [ConversationContent] string reason, string result);
}

/// <summary>Reads the ID out of a tool result (a part order or a charge), or the error the tool reported.</summary>
public static class ToolResults
{
    public static string Describe(object? result) => result switch
    {
        JsonElement { ValueKind: JsonValueKind.Object } json when json.TryGetProperty("id", out var id) => id.GetString() ?? "?",
        JsonElement { ValueKind: JsonValueKind.Object } json when json.TryGetProperty("error", out var error) => $"error: {error.GetString()}",
        null => "-",
        _ => Convert.ToString(result, CultureInfo.InvariantCulture) ?? "-",
    };
}
