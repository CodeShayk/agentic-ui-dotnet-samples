// Copied from Chapter 14 (Ch14.Beacon.Agent/Approvals); each chapter's code is a self-contained checkpoint.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Ch18.Beacon.Agent.Approvals;

/// <summary>What happened to an approval, or to a consequential call.</summary>
public enum ApprovalEvent
{
    Requested,
    Approved,
    Edited,
    Denied,
    Cancelled,
    Withdrawn,
    Executed,
    Blocked,
    Refused,
}

/// <summary>
/// One line of the approval audit: what happened, when, on which thread and run, from which client, and
/// to which call. <see cref="ClientId"/> is the client's own label until Chapter 23 puts a user on every request.
/// </summary>
public sealed record ApprovalAuditEntry(
    DateTimeOffset At,
    ApprovalEvent Event,
    string ThreadId,
    string RunId,
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
    public static ApprovalAuditEntry For(ApprovalEvent kind, PendingApproval approval, DateTimeOffset at, string runId, string clientId) => new(
        at,
        kind,
        approval.ThreadId,
        runId,
        clientId,
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
/// Keeps the audit in memory and writes each entry to the log as one structured line. A production audit
/// belongs in an append-only store that the agent can write and nobody can edit.
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
            entry.ClientId,
            entry.At.ToString("O", CultureInfo.InvariantCulture),
            entry.Reason ?? "-",
            entry.Result ?? "-");
    }

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Information,
        Message = "{Event} {ApprovalId} {ToolName} {Arguments} {Amount} | thread {ThreadId} run {RunId} client {ClientId} at {At} | reason: {Reason} | result: {Result}")]
    private static partial void Log(
        ILogger logger, ApprovalEvent @event, string approvalId, string toolName, string arguments, string amount,
        string threadId, string runId, string clientId, string at, string reason, string result);
}

/// <summary>Reads the ID out of a tool result (a part order or a charge), or the error the tool reported.</summary>
public static class ToolResults
{
    public static string Describe(object? result) => result switch
    {
        JsonElement { ValueKind: JsonValueKind.Object } json when json.TryGetProperty("id", out var id) => id.GetString() ?? "?",
        JsonElement { ValueKind: JsonValueKind.Object } json when json.TryGetProperty("error", out var error) => $"error: {error.GetString()}",
        null => "-",
        _ => result.ToString() ?? "-",
    };
}
