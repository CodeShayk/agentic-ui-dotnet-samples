using AGUI.Abstractions;
using AGUI.Formatting;

namespace Ch06.Tests;

/// <summary>
/// Streams captured from Ch06.Beacon.Agent with <c>curl -N</c> (see the Recordings folder), read
/// back with the SDK's own SSE reader so the tests see exactly what AGUIChatClient would.
/// </summary>
public static class Recordings
{
    public const string WorkOrderStatus = "wo-1001-status.sse";
    public const string HandoverNote = "handover-note.sse";

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
