using AGUI.Abstractions;
using AGUI.Formatting;

namespace Ch09.Tests.Infrastructure;

/// <summary>
/// Streams captured from Ch09.Beacon.Agent on port 5090 with <c>curl -N</c>-style captures (see the
/// Recordings folder), read back with the SDK's own SSE reader, as AGUIChatClient reads a live stream.
/// </summary>
public static class Recordings
{
    /// <summary>"What's the status of WO-1001?": one call, arguments streamed in five pieces.</summary>
    public const string WorkOrderStatus = "agent-wo-1001-status.sse";

    /// <summary>"Prepare WO-1001 for Priya": three calls in one model turn, all succeeding.</summary>
    public const string PrepareWo1001 = "agent-prepare-wo-1001.sse";

    /// <summary>"Prepare WO-1005 for Elena": three calls; the warranty service is down for EQ-701.</summary>
    public const string PrepareWo1005 = "agent-prepare-wo-1005.sse";

    /// <summary>"Mark C3 done on WO-1001": the checklist write.</summary>
    public const string Checklist = "agent-checklist.sse";

    public static async Task<IReadOnlyList<BaseEvent>> LoadAsync(string name)
    {
        await using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Recordings", name));
        List<BaseEvent> events = [];
        await foreach (var evt in new SseEventStreamFormatter().ReadAsync(file, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        return events;
    }
}
