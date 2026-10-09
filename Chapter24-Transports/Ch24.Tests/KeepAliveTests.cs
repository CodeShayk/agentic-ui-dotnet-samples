using System.Text;
using AGUI.Abstractions;
using Beacon.Testing;

namespace Ch24.Tests;

public sealed class KeepAliveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Each model call stays silent for a second before its first chunk; heartbeats are due every 200 ms, so a
    // busy machine can delay a timer a long way before a silence goes without one.
    private static AgentHost SlowModel(string heartbeat) => new(
        ("Beacon:Streaming:HeartbeatInterval", heartbeat),
        ("Beacon:Simulation:FirstChunkDelay", "00:00:01"));

    [Fact]
    public async Task Silences_longer_than_the_interval_carry_comment_lines_between_whole_frames()
    {
        await using var host = SlowModel("00:00:00.200");
        using var http = host.CreateClient();

        using var response = await AgentHost.PostAsync(http, AgentHost.Run(), AgentHost.SseType, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var blocks = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        // Every block is either one whole event or one comment; nothing is ever spliced into a frame.
        Assert.All(blocks, block => Assert.True(
            block == ": keep-alive" || (block.StartsWith("data: {", StringComparison.Ordinal) && !block.Contains('\n', StringComparison.Ordinal)),
            $"Unexpected block: {block}"));

        // Two silences of about a second each, after RUN_STARTED and after the tool result: at least one comment in each.
        var comments = blocks.Count(b => b == ": keep-alive");
        Assert.True(comments >= 2, $"{comments} comment(s).");
        Assert.StartsWith("data: {\"type\":\"RUN_STARTED\"", blocks[0], StringComparison.Ordinal);
        Assert.Equal(": keep-alive", blocks[1]);

        // An SSE consumer ignores comments: the SDK's reader sees only the events.
        var events = await ReadSseAsync(body);
        Assert.Equal(blocks.Length - comments, events.Count);
        Assert.IsType<RunFinishedEvent>(events[^1]);
    }

    [Fact]
    public async Task Protobuf_streams_get_no_heartbeats_because_the_binding_has_no_comment_frame()
    {
        await using var host = SlowModel("00:00:00.200");
        using var http = host.CreateClient();

        using var response = await AgentHost.PostAsync(http, AgentHost.Run(), AgentHost.ProtobufType, Ct);
        var body = await response.Content.ReadAsByteArrayAsync(Ct);

        // The body is length-prefixed frames end to end, with no byte left over.
        var offset = 0;
        var frames = 0;
        while (offset < body.Length)
        {
            var length = (body[offset] << 24) | (body[offset + 1] << 16) | (body[offset + 2] << 8) | body[offset + 3];
            offset += 4 + length;
            frames++;
        }

        Assert.Equal(body.Length, offset);
        Assert.Equal(15, frames);
    }

    [Fact]
    public async Task Heartbeats_can_be_switched_off()
    {
        await using var host = SlowModel("00:00:00");
        using var http = host.CreateClient();

        var frames = await AGUIWire.RunRawAsync(http, "/agent", AgentHost.Run(), Ct);
        using var response = await AgentHost.PostAsync(http, AgentHost.Run(), AgentHost.SseType, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain(": keep-alive", body, StringComparison.Ordinal);
        Assert.Equal(15, frames.Count);
    }

    private static async Task<List<BaseEvent>> ReadSseAsync(string body)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        List<BaseEvent> events = [];
        await foreach (var evt in new AGUI.Formatting.SseEventStreamFormatter().ReadAsync(stream, Ct))
        {
            events.Add(evt);
        }

        return events;
    }
}
