using AGUI.Abstractions;
using AGUI.Formatting;

namespace Ch07.Tests;

/// <summary>
/// Streams captured with <c>curl -N</c> (see the Recordings folder): <c>tour-*.sse</c> from Chapter 3's
/// event tour on port 5030, one per event family, and <c>agent-*.sse</c> from this chapter's agent on
/// port 5070. They are read back with the SDK's own SSE reader, exactly as AGUIChatClient reads a live stream.
/// </summary>
public static class Recordings
{
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
