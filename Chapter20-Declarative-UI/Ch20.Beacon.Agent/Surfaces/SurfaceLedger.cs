using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Ch20.Beacon.Agent.Inspections;
using Microsoft.Extensions.Options;

namespace Ch20.Beacon.Agent.Surfaces;

public sealed class SurfaceOptions
{
    public const string SectionName = "Surfaces";

    /// <summary>How long an issued surface accepts its action. A form left open over lunch still works.</summary>
    public int LifetimeMinutes { get; set; } = 120;

    /// <summary>How many surfaces the model may try to describe in one run before it must answer in text.</summary>
    public int MaxAttemptsPerRun { get; set; } = 3;
}

public enum SurfaceRecordStatus { Open, Submitted }

/// <summary>
/// What the server knows about a surface it sent: whose thread, which form, which button may act on it and
/// until when. Activity messages never come back from the client (the spec forbids it), so this record is
/// the server's only memory of the surface, and the only thing an action is checked against.
/// </summary>
public sealed class SurfaceRecord(string threadId, string surfaceId, InspectionForm form, string callId, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
{
    public string ThreadId { get; } = threadId;

    public string SurfaceId { get; } = surfaceId;

    public InspectionForm Form { get; } = form;

    public string CallId { get; } = callId;

    public DateTimeOffset IssuedAt { get; } = issuedAt;

    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    public SurfaceRecordStatus Status { get; private set; } = SurfaceRecordStatus.Open;

    public string? SubmittedRunId { get; private set; }

    public string? Summary { get; private set; }

    internal Lock Gate { get; } = new();

    internal void MarkSubmitted(string runId, string summary) => (Status, SubmittedRunId, Summary) = (SurfaceRecordStatus.Submitted, runId, summary);
}

/// <summary>
/// The server's record of the surfaces it issued, by thread, and the operations waiting to be painted by
/// the tool call that produced them. In memory, like Beacon's other stores; Chapter 22 makes such records
/// durable and Chapter 23 binds them to the signed-in user.
/// </summary>
public sealed class SurfaceLedger(TimeProvider time, IOptions<SurfaceOptions> options)
{
    private readonly ConcurrentDictionary<(string Thread, string Surface), SurfaceRecord> _records = new();
    private readonly ConcurrentDictionary<string, JsonArray> _paint = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Thread, string Run), int> _attempts = new();

    public int MaxAttemptsPerRun => options.Value.MaxAttemptsPerRun;

    public TimeProvider Time => time;

    /// <summary>Records an accepted surface. A newer surface with the same ID on the same thread replaces the older.</summary>
    public SurfaceRecord Issue(string threadId, string surfaceId, InspectionForm form, string callId, JsonArray operations)
    {
        var now = time.GetUtcNow();
        var record = new SurfaceRecord(threadId, surfaceId, form, callId, now, now.AddMinutes(options.Value.LifetimeMinutes));
        _records[(threadId, surfaceId)] = record;
        _paint[callId] = operations;
        return record;
    }

    /// <summary>The operations a tool call's result paints, once. A run that ends early leaves them unclaimed.</summary>
    public JsonArray? TakePaint(string callId) => _paint.TryRemove(callId, out var operations) ? operations : null;

    public SurfaceRecord? Find(string threadId, string surfaceId) => _records.GetValueOrDefault((threadId, surfaceId));

    /// <summary>Counts one attempt to describe a surface in a run, and returns the count so far.</summary>
    public int CountAttempt(string threadId, string runId) => _attempts.AddOrUpdate((threadId, runId), 1, (_, count) => count + 1);
}
