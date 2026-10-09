// Adapted from Chapter 22 (Ch22.Beacon.Agent). Chapter 23's transcript is linear and keyed by user, so a thread
// header names its owner and its latest run, and a run record holds what the run accepted and produced.
using System.Text.Json;
using AGUI.Abstractions;
using Ch27.Beacon.Agent.Security.History;

namespace Ch27.Beacon.Agent.Conversations;

/// <summary>One run as the server keeps it: the messages it accepted, the messages it produced and how it ended.</summary>
public sealed record RunRecord
{
    public required string RunId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public RunEnding Ending { get; init; }

    /// <summary>The <c>RUN_ERROR</c> message and code, when <see cref="Ending"/> is <see cref="RunEnding.Failed"/>.</summary>
    public string? Error { get; init; }

    public string? ErrorCode { get; init; }

    /// <summary>The client's messages the server accepted: new user turns and results for calls it was waiting on.</summary>
    public IList<AGUIMessage> Input { get; init; } = [];

    /// <summary>What the run produced, exactly as the client was shown it.</summary>
    public IList<AGUIMessage> Output { get; init; } = [];
}

/// <summary>A run that started and hasn't been recorded yet, with what it accepted. Set at load means the process stopped mid-run.</summary>
public sealed record OpenRun(string RunId, DateTimeOffset StartedAt, IList<AGUIMessage> Input);

/// <summary>What the store knows about one user's thread without reading its runs: enough to list it and resume it.</summary>
public sealed record ConversationHeader
{
    /// <summary>The authenticated user the conversation belongs to (the token's subject).</summary>
    public required string UserId { get; init; }

    public required string ThreadId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string? LastRunId { get; init; }

    public RunEnding LastRunEnding { get; init; }

    public string? LastRunError { get; init; }

    public string? LastRunErrorCode { get; init; }

    public OpenRun? OpenRun { get; init; }

    /// <summary>Frontend tool calls the last run left with the client, by call ID, with the tool's name.</summary>
    public IDictionary<string, string> PendingClientCalls { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>One line of the event log: an event as it went out, with the run it belongs to.</summary>
public sealed record StoredEvent(string RunId, DateTimeOffset At, JsonElement Event);
