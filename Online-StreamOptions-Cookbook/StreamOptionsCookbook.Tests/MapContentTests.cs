using StreamOptionsCookbook;
using StreamOptionsCookbook.Tests.Infrastructure;
using Beacon.Testing;

namespace StreamOptionsCookbook.Tests;

/// <summary>Recipe 1: <c>MapContent</c> turns the pipeline's own content into events.</summary>
public sealed class MapContentTests
{
    [Fact]
    public async Task Phase_markers_become_step_events()
    {
        var model = new RecordedChatClient(Turns.Text("msg-1", "The run capacitor ", "is the likely cause."));
        await using var host = await CookbookHost.StartAsync(new PhaseMarkingChatClient(model, "diagnose"), MapContentRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Why is WO-1001 short-cycling?"));
        frames.Capture("01-map-content");

        // Mapped events don't close the open text message: STEP_FINISHED lands before TEXT_MESSAGE_END,
        // which the spec allows (steps and messages are independent items).
        Assert.Equal(
            ["RUN_STARTED", "STEP_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_CONTENT",
             "STEP_FINISHED", "TEXT_MESSAGE_END", "RUN_FINISHED"],
            frames.Types());
        Assert.All(frames.Where(f => f.Type().StartsWith("STEP_", StringComparison.Ordinal)), step =>
        {
            Assert.Equal("diagnose", step.Str("stepName"));
            Assert.False(step.HasRawEvent()); // mapped events go out as built
        });
    }

    [Fact]
    public async Task Content_the_endpoint_cannot_serialize_ends_the_run_before_any_mapper_runs()
    {
        var model = new RecordedChatClient(Turns.Text("msg-1", "The run capacitor is the likely cause."));
        await using var host = await CookbookHost.StartAsync(
            new PhaseMarkingChatClient(model, "diagnose"), MapContentRecipe.Apply, registerContentTypes: false);

        var frames = await host.RunAsync(Turns.Run("Why is WO-1001 short-cycling?"));

        Assert.Equal(["RUN_STARTED", "RUN_ERROR"], frames.Types());
        Assert.Equal("StreamingError", frames[1].Str("code"));
    }

    [Fact]
    public async Task Mappers_run_in_order_null_passes_and_empty_swallows()
    {
        // A first mapper that returns null for everything: the recipe's mapper still sees the markers.
        var passing = new RecordedChatClient(Turns.Text("msg-1", "Done."));
        await using (var host = await CookbookHost.StartAsync(
            new PhaseMarkingChatClient(passing, "diagnose"),
            options => MapContentRecipe.Apply(options.MapContent(_ => null))))
        {
            var frames = await host.RunAsync(Turns.Run("Diagnose WO-1001."));
            Assert.Contains("STEP_STARTED", frames.Types());
            Assert.Contains("STEP_FINISHED", frames.Types());
        }

        // A first mapper that returns an empty sequence has handled the content: nothing is written and the
        // recipe's mapper is never asked.
        var swallowing = new RecordedChatClient(Turns.Text("msg-1", "Done."));
        await using (var host = await CookbookHost.StartAsync(
            new PhaseMarkingChatClient(swallowing, "diagnose"),
            options => MapContentRecipe.Apply(options.MapContent(content => content is PhaseContent ? [] : null))))
        {
            var frames = await host.RunAsync(Turns.Run("Diagnose WO-1001."));
            Assert.DoesNotContain(frames, f => f.Type().StartsWith("STEP_", StringComparison.Ordinal));
            Assert.Equal("RUN_FINISHED", frames[^1].Type());
        }
    }
}
