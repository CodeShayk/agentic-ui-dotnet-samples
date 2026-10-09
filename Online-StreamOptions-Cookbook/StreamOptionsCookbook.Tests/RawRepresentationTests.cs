using AGUI.Abstractions;
using StreamOptionsCookbook;
using StreamOptionsCookbook.Tests.Infrastructure;
using Beacon.Testing;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook.Tests;

/// <summary>Recipe 9: a <see cref="BaseEvent"/> in <c>RawRepresentation</c> goes out verbatim.</summary>
public sealed class RawRepresentationTests
{
    [Fact]
    public async Task Activity_events_from_a_delegating_client_are_written_verbatim()
    {
        var model = new RecordedChatClient(Turns.Text("msg-1", "CAP-45-5 is in stock ", "in Portsmouth."));
        var pipeline = new ChatClientBuilder(model).UseProgressActivity("Checking parts for WO-1001").Build();
        await using var host = await CookbookHost.StartAsync(pipeline);

        var frames = await host.RunAsync(Turns.Run("Is CAP-45-5 in stock?"));
        frames.Capture("09-raw-representation");

        // RUN_STARTED comes first even though the first update was a raw event. Raw events don't close the
        // open text message, so the delta arrives before TEXT_MESSAGE_END.
        Assert.Equal(
            ["RUN_STARTED", "ACTIVITY_SNAPSHOT", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_CONTENT",
             "ACTIVITY_DELTA", "TEXT_MESSAGE_END", "RUN_FINISHED"],
            frames.Types());
        Assert.Equal("activity-run-1", frames.Only("ACTIVITY_SNAPSHOT").Str("messageId"));
        Assert.Equal("activity-run-1", frames.Only("ACTIVITY_DELTA").Str("messageId"));
        Assert.False(frames.Only("ACTIVITY_SNAPSHOT").HasRawEvent());
    }

    [Fact]
    public async Task Content_in_the_same_update_as_a_raw_event_is_dropped()
    {
        var model = new RecordedChatClient(
        [
            new ChatResponseUpdate(ChatRole.Assistant, "This sentence never reaches the client. ")
            {
                MessageId = "msg-1",
                RawRepresentation = new CustomEvent { Name = "beacon.note", Value = Frames.Json("""{"workOrderId":"WO-1001"}""") },
            },
            .. Turns.Text("msg-1", "This one does."),
        ]);
        await using var host = await CookbookHost.StartAsync(model);

        var frames = await host.RunAsync(Turns.Run("Anything new on WO-1001?"));

        Assert.Equal("beacon.note", frames.Only("CUSTOM").Str("name"));
        Assert.Equal("This one does.", frames.Only("TEXT_MESSAGE_CONTENT").Str("delta"));
    }
}
