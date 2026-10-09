using StreamOptionsCookbook;
using StreamOptionsCookbook.Tests.Infrastructure;
using Beacon.Testing;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook.Tests;

/// <summary>Recipe 2: <c>MapInterrupt</c> turns the pipeline's own request into an interrupt outcome.</summary>
public sealed class MapInterruptTests
{
    private static readonly PendingConfirmation Reassign = new(
        "cnf_8f2c41d07a9e4b6c",
        "Move WO-1001 from Priya Raman to Marcus Bell at 13:00?",
        new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_confirmation_request_becomes_an_interrupt_outcome()
    {
        var model = new RecordedChatClient(
        [
            .. Turns.Text("msg-1", "Priya is still at Harbor View Dental. ", "Marcus is free at 13:00."),
            Turns.Content(new ConfirmationRequestContent { ConfirmationId = Reassign.Id, Request = Reassign }),
        ]);
        await using var host = await CookbookHost.StartAsync(model, MapInterruptRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Can someone else take WO-1001 this afternoon?"));
        frames.Capture("02-map-interrupt");

        Assert.Equal(
            ["RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED"],
            frames.Types());

        var outcome = frames[^1].GetProperty("outcome");
        Assert.Equal("interrupt", outcome.Str("type"));
        var interrupt = Assert.Single(outcome.GetProperty("interrupts").EnumerateArray());
        Assert.Equal(Reassign.Id, interrupt.Str("id"));
        Assert.Equal("confirmation", interrupt.Str("reason"));
        Assert.Equal(Reassign.Question, interrupt.Str("message"));
        Assert.Equal("2026-11-16T09:30:00Z", interrupt.Str("expiresAt"));
        Assert.Equal("boolean", interrupt.GetProperty("responseSchema").GetProperty("properties").GetProperty("confirmed").Str("type"));
        Assert.False(interrupt.TryGetProperty("toolCallId", out _));
    }

    [Fact]
    public async Task The_run_does_not_stop_at_the_interrupt_content()
    {
        // The interrupt is collected, not emitted: whatever the pipeline streams after it still goes out,
        // and RUN_FINISHED comes only when the update stream ends. The layer that asks must stop the run itself.
        var model = new RecordedChatClient(
        [
            Turns.Content(new ConfirmationRequestContent { ConfirmationId = Reassign.Id, Request = Reassign }),
            .. Turns.Text("msg-2", "Meanwhile, here is the schedule."),
        ]);
        await using var host = await CookbookHost.StartAsync(model, MapInterruptRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Can someone else take WO-1001 this afternoon?"));

        Assert.Equal(["RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED"], frames.Types());
        Assert.Equal("interrupt", frames[^1].GetProperty("outcome").Str("type"));
    }

    [Fact]
    public async Task Content_claimed_by_an_interrupt_mapper_never_reaches_MapContent()
    {
        var model = new RecordedChatClient(
        [
            Turns.Content(new ConfirmationRequestContent { ConfirmationId = Reassign.Id, Request = Reassign }),
        ]);
        var contentMapperCalls = 0;
        await using var host = await CookbookHost.StartAsync(model, options => MapInterruptRecipe.Apply(options)
            .MapContent(content =>
            {
                contentMapperCalls++;
                return null;
            }));

        var frames = await host.RunAsync(Turns.Run("Can someone else take WO-1001 this afternoon?"));

        Assert.Equal(["RUN_STARTED", "RUN_FINISHED"], frames.Types());
        Assert.Equal(0, contentMapperCalls);
    }
}
