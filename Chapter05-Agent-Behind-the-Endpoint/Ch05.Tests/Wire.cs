using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Testing;

namespace Ch05.Tests;

/// <summary>Helpers for reading and judging what a host put on the wire.</summary>
internal static class Wire
{
    /// <summary>
    /// The run's event types with repeats collapsed (one <c>TEXT_MESSAGE_CONTENT</c> stands for
    /// many) and text messages that never received content left out. Empty messages are legal
    /// (a message takes zero or more content events) and some providers produce them.
    /// </summary>
    public static IReadOnlyList<string> Shape(IReadOnlyList<BaseEvent> events)
    {
        HashSet<string> withContent = [.. events.OfType<TextMessageContentEvent>().Select(e => e.MessageId)];

        List<string> shape = [];
        foreach (var evt in events)
        {
            var emptyMessage = evt switch
            {
                TextMessageStartEvent start => !withContent.Contains(start.MessageId),
                TextMessageEndEvent end => !withContent.Contains(end.MessageId),
                _ => false,
            };

            var type = AGUIWire.ToWireJson(evt).GetProperty("type").GetString()!;
            if (!emptyMessage && (shape.Count == 0 || shape[^1] != type))
            {
                shape.Add(type);
            }
        }

        return shape;
    }

    /// <summary>Asserts the ordering rules every run must keep, whichever model produced it.</summary>
    public static void AssertWellFormed(IReadOnlyList<BaseEvent> events, string threadId, string runId)
    {
        var started = Assert.IsType<RunStartedEvent>(events[0]);
        Assert.Equal(threadId, started.ThreadId);
        Assert.Equal(runId, started.RunId);

        var finished = Assert.IsType<RunFinishedEvent>(events[^1]);
        Assert.Equal(threadId, finished.ThreadId);
        Assert.Equal(runId, finished.RunId);
        Assert.Single(events, e => e is RunFinishedEvent or RunErrorEvent);

        HashSet<string> openMessages = [];
        HashSet<string> openToolCalls = [];
        HashSet<string> endedToolCalls = [];
        foreach (var evt in events)
        {
            switch (evt)
            {
                case TextMessageStartEvent e:
                    Assert.True(openMessages.Add(e.MessageId), $"Message {e.MessageId} opened twice.");
                    break;
                case TextMessageContentEvent e:
                    Assert.Contains(e.MessageId, openMessages);
                    break;
                case TextMessageEndEvent e:
                    Assert.True(openMessages.Remove(e.MessageId), $"Message {e.MessageId} closed but not open.");
                    break;
                case ToolCallStartEvent e:
                    Assert.True(openToolCalls.Add(e.ToolCallId), $"Tool call {e.ToolCallId} opened twice.");
                    break;
                case ToolCallArgsEvent e:
                    Assert.Contains(e.ToolCallId, openToolCalls);
                    break;
                case ToolCallEndEvent e:
                    Assert.True(openToolCalls.Remove(e.ToolCallId), $"Tool call {e.ToolCallId} closed but not open.");
                    endedToolCalls.Add(e.ToolCallId);
                    break;
                case ToolCallResultEvent e:
                    Assert.Contains(e.ToolCallId, endedToolCalls);
                    break;
            }
        }

        Assert.Empty(openMessages);
        Assert.Empty(openToolCalls);
    }

    /// <summary>The text the run's messages carried, concatenated.</summary>
    public static string Text(IEnumerable<BaseEvent> events) =>
        string.Concat(events.OfType<TextMessageContentEvent>().Select(e => e.Delta));

    /// <summary>
    /// POSTs raw JSON and returns each SSE frame's JSON as written, before any AG-UI reader touches it.
    /// The SDK's reader treats an explicit null as absent, so it would hide the bug a null check looks for.
    /// </summary>
    public static async Task<IReadOnlyList<JsonElement>> RawFramesAsync(
        HttpClient http,
        string runAgentInputJson,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = new StringContent(runAgentInputJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        List<JsonElement> frames = [];
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(item.Data);
            frames.Add(document.RootElement.Clone());
        }

        return frames;
    }
}
