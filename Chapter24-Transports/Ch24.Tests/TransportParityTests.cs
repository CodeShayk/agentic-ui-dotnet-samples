using System.Text.Json;
using Beacon.Testing;

namespace Ch24.Tests;

public sealed class TransportParityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("What is the status of WO-1001?", true)]   // a tool call, its result and an answer
    [InlineData("Why is WO-1001 short-cycling?", true)]    // reasoning, then an answer
    [InlineData("What is the status of WO-1001?", false)]  // with rawEvent kept, an open JSON payload
    public async Task Sse_and_protobuf_carry_the_same_events(string question, bool stripRawEvent)
    {
        await using var host = new AgentHost(("Beacon:Streaming:StripRawEvent", stripRawEvent ? "true" : "false"));
        using var http = host.CreateClient();

        using var sse = await AgentHost.PostAsync(http, AgentHost.Run(question), AgentHost.SseType, Ct);
        var overSse = AgentHost.Normalized(await AgentHost.ReadEventsAsync(sse, Ct));

        using var protobuf = await AgentHost.PostAsync(http, AgentHost.Run(question), AgentHost.ProtobufType, Ct);
        var overProtobuf = AgentHost.Normalized(await AgentHost.ReadEventsAsync(protobuf, Ct));

        Assert.Equal(AgentHost.ProtobufType, protobuf.Content.Headers.ContentType?.MediaType);
        Assert.Equal(overSse.Count, overProtobuf.Count);
        for (var i = 0; i < overSse.Count; i++)
        {
            Assert.True(
                JsonElement.DeepEquals(overSse[i], overProtobuf[i]),
                $"Event {i} differs.\nSSE:      {overSse[i]}\nprotobuf: {overProtobuf[i]}");
        }

        Assert.Equal(stripRawEvent, overSse.All(e => !e.TryGetProperty("rawEvent", out _)));
    }

    [Fact]
    public async Task Stripping_rawEvent_leaves_no_rawEvent_and_no_nulls_on_the_wire()
    {
        await using var host = new AgentHost();
        using var http = host.CreateClient();

        var frames = await AGUIWire.RunRawAsync(http, "/agent", AgentHost.Run(), Ct);

        Assert.All(frames, frame => Assert.False(frame.TryGetProperty("rawEvent", out _)));
        Assert.All(frames, frame => Assert.Empty(AGUIWire.NullProperties(frame)));
    }
}
