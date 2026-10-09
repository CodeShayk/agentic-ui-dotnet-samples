using AGUI.Abstractions;
using AGUI.Formatting;

namespace Ch13.Tests.Infrastructure;

/// <summary>
/// Streams captured from Ch13.Beacon.Agent on port 5130 with <c>curl -N</c>, read back with the SDK's
/// own SSE reader, exactly as AGUIChatClient reads a live stream.
/// </summary>
public static class Recordings
{
    /// <summary>"Diagnose WO-1001 and draft the service report.": plan, four lookups, the report, the answer.</summary>
    public const string Diagnose = "diagnose-wo-1001.sse";

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
