using System.Text.Json;
using AGUI.Server;
using StreamOptionsCookbook;
using StreamOptionsCookbook.Tests.Infrastructure;
using Beacon.Testing;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook.Tests;

/// <summary>Recipe 8: <c>WithUsageProvider</c> labels the run's token usage.</summary>
public sealed class UsageTests
{
    [Fact]
    public async Task Usage_from_every_model_call_is_summed_and_labeled()
    {
        var model = new RecordedChatClient(
            [.. Turns.Call("call-1", MapResultRecipe.CheckWarrantyName, """{"equipmentId":"EQ-501"}"""), Turns.Usage(412, 18)],
            [.. Turns.Text("msg-2", "EQ-501's parts are covered until May 14, 2029."), Turns.Usage(486, 31)]);
        var pipeline = new ProviderLabeledChatClient(Pipelines.WithTools(model, MapResultRecipe.CheckWarranty), "beacon");
        await using var host = await CookbookHost.StartAsync(pipeline, options => UsageProviderRecipe.Apply(options, pipeline));

        var frames = await host.RunAsync(Turns.Run("Is EQ-501 still under warranty?"));
        frames.Capture("08-usage");

        var usage = Assert.Single(frames[^1].GetProperty("usage").EnumerateArray());
        Assert.True(Frames.Json("""{"provider":"beacon","model":"beacon-scripted","inputTokens":898,"outputTokens":49,"totalTokens":947}""").SameJson(usage));
    }

    [Fact]
    public async Task Each_model_gets_its_own_entry_and_no_label_means_no_provider()
    {
        var model = new RecordedChatClient(
            [.. Turns.Call("call-1", MapResultRecipe.CheckWarrantyName, """{"equipmentId":"EQ-501"}"""), Turns.Usage(412, 18, "beacon-router")],
            [.. Turns.Text("msg-2", "EQ-501's parts are covered until May 14, 2029."), Turns.Usage(486, 31)]);
        await using var host = await CookbookHost.StartAsync(Pipelines.WithTools(model, MapResultRecipe.CheckWarranty));

        var frames = await host.RunAsync(Turns.Run("Is EQ-501 still under warranty?"));

        var usage = frames[^1].GetProperty("usage").EnumerateArray().ToList();
        Assert.Equal(["beacon-router", "beacon-scripted"], usage.Select(u => u.Str("model")));
        Assert.All(usage, u => Assert.False(u.TryGetProperty("provider", out _)));
    }

    [Fact]
    public async Task A_run_that_fails_reports_the_usage_so_far_on_RUN_ERROR()
    {
        // No second recorded turn: the model call after the tool fails, and the run ends in RUN_ERROR.
        var model = new RecordedChatClient(
            [.. Turns.Call("call-1", MapResultRecipe.CheckWarrantyName, """{"equipmentId":"EQ-501"}"""), Turns.Usage(412, 18)]);
        var pipeline = new ProviderLabeledChatClient(Pipelines.WithTools(model, MapResultRecipe.CheckWarranty), "beacon");
        await using var host = await CookbookHost.StartAsync(pipeline, options => UsageProviderRecipe.Apply(options, pipeline));

        var frames = await host.RunAsync(Turns.Run("Is EQ-501 still under warranty?"));

        Assert.Equal("RUN_ERROR", frames[^1].Type());
        var usage = Assert.Single(frames[^1].GetProperty("usage").EnumerateArray());
        Assert.Equal(("beacon", 412L), (usage.Str("provider"), usage.GetProperty("inputTokens").GetInt64()));
    }
}
