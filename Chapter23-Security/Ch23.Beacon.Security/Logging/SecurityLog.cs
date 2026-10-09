using Microsoft.Extensions.Logging;

namespace Ch23.Beacon.Security.Logging;

/// <summary>
/// Every security decision Beacon logs (the approval audit logs its own entries the same way). User IDs
/// are classified as personal data, so the redaction configured at startup applies to them wherever
/// these entries go: the console, a file or an OpenTelemetry exporter.
/// </summary>
public static partial class SecurityLog
{
    [LoggerMessage(2301, LogLevel.Warning,
        "Ignored a client-sent {Role} message {MessageId} on thread {ThreadId} for user {UserId}: the server did not write it.")]
    public static partial void IgnoredUnknownMessage(
        ILogger logger, string role, string messageId, string threadId, [PersonalData] string userId);

    [LoggerMessage(2302, LogLevel.Warning,
        "Ignored a changed copy of message {MessageId} on thread {ThreadId} for user {UserId}; the server's copy was used.")]
    public static partial void IgnoredChangedCopy(
        ILogger logger, string messageId, string threadId, [PersonalData] string userId);

    [LoggerMessage(2303, LogLevel.Warning,
        "Refused a run on thread {ThreadId} for user {UserId}: {Reason}")]
    public static partial void RefusedRun(
        ILogger logger, string threadId, [PersonalData] string userId, string reason);

    [LoggerMessage(2304, LogLevel.Warning,
        "Blocked {ToolName} call {CallId} for user {UserId}: no approval was granted for this exact call.")]
    public static partial void BlockedUngrantedCall(
        ILogger logger, string toolName, string callId, [PersonalData] string userId);

    [LoggerMessage(2307, LogLevel.Warning,
        "Run {RunId} on thread {ThreadId} for user {UserId} exceeded its {Seconds}-second limit and was stopped.")]
    public static partial void RunTimedOut(
        ILogger logger, string runId, string threadId, [PersonalData] string userId, int seconds);

    [LoggerMessage(2309, LogLevel.Warning,
        "A run for user {UserId} exceeded its {Seconds}-second limit and was stopped.")]
    public static partial void AgentRunTimedOut(
        ILogger logger, [PersonalData] string userId, int seconds);

    [LoggerMessage(2308, LogLevel.Warning,
        "Ignored a client-sent {Role} message {MessageId} for user {UserId}: this endpoint takes only user messages from the client.")]
    public static partial void DroppedNonUserMessage(
        ILogger logger, string role, string messageId, [PersonalData] string userId);
}
