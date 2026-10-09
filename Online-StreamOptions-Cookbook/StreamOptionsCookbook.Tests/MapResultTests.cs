using StreamOptionsCookbook;
using StreamOptionsCookbook.Tests.Infrastructure;
using Beacon.Testing;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook.Tests;

/// <summary>Recipe 4: <c>MapResult</c> lets a tool's result drive more of the UI.</summary>
public sealed class MapResultTests
{
    private const string Eq501 = """{"equipmentId":"EQ-501"}""";

    [Fact]
    public async Task A_warranty_result_also_becomes_a_custom_event()
    {
        var model = new RecordedChatClient(
            Turns.Call("call-1", MapResultRecipe.CheckWarrantyName, Eq501),
            Turns.Text("msg-2", "EQ-501's parts are covered until May 14, 2029."));
        await using var host = await CookbookHost.StartAsync(Pipelines.WithTools(model, MapResultRecipe.CheckWarranty), MapResultRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Is EQ-501 still under warranty?"));
        frames.Capture("04-map-result");

        var result = frames.IndexOf("TOOL_CALL_RESULT");
        Assert.Equal("CUSTOM", frames[result + 1].Type());
        Assert.Equal(MapResultRecipe.WarrantyCheckedEvent, frames[result + 1].Str("name"));
        Assert.True(Frames.Json(frames[result].Str("content")!).SameJson(frames[result + 1].GetProperty("value")));
        Assert.Equal("RUN_FINISHED", frames[^1].Type());
    }

    [Fact]
    public async Task A_failed_call_reaches_the_mapper_as_an_error_string_and_maps_to_nothing()
    {
        var outage = AIFunctionFactory.Create(
            WarrantyCheck (string equipmentId) => throw new HttpRequestException("Warranty service unavailable."),
            MapResultRecipe.CheckWarrantyName);
        var model = new RecordedChatClient(
            Turns.Call("call-1", MapResultRecipe.CheckWarrantyName, Eq501),
            Turns.Text("msg-2", "I couldn't reach the warranty service."));
        await using var host = await CookbookHost.StartAsync(Pipelines.WithTools(model, outage), MapResultRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Is EQ-501 still under warranty?"));

        Assert.Equal("Error: Function failed.", frames.Only("TOOL_CALL_RESULT").Str("content"));
        Assert.DoesNotContain(frames, f => f.Type() == "CUSTOM");
        Assert.Equal("RUN_FINISHED", frames[^1].Type());
    }

    [Fact]
    public async Task A_mapper_that_returns_null_ends_the_run_in_RUN_ERROR()
    {
        var model = new RecordedChatClient(
            Turns.Call("call-1", MapResultRecipe.CheckWarrantyName, Eq501),
            Turns.Text("msg-2", "EQ-501's parts are covered until May 14, 2029."));
        await using var host = await CookbookHost.StartAsync(
            Pipelines.WithTools(model, MapResultRecipe.CheckWarranty),
            options => options.MapResult(MapResultRecipe.CheckWarrantyName, _ => null!));

        var frames = await host.RunAsync(Turns.Run("Is EQ-501 still under warranty?"));

        Assert.Equal(["RUN_STARTED", "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "TOOL_CALL_RESULT", "RUN_ERROR"], frames.Types());
        Assert.Equal("StreamingError", frames[^1].Str("code"));
    }

    [Fact]
    public async Task Only_the_last_mapper_registered_for_a_tool_counts()
    {
        var model = new RecordedChatClient(
            Turns.Call("call-1", MapResultRecipe.CheckWarrantyName, Eq501),
            Turns.Text("msg-2", "EQ-501's parts are covered until May 14, 2029."));
        await using var host = await CookbookHost.StartAsync(
            Pipelines.WithTools(model, MapResultRecipe.CheckWarranty),
            options => MapResultRecipe.Apply(options).MapResult(MapResultRecipe.CheckWarrantyName, _ => []));

        var frames = await host.RunAsync(Turns.Run("Is EQ-501 still under warranty?"));

        Assert.DoesNotContain(frames, f => f.Type() == "CUSTOM");
    }

    [Fact]
    public async Task A_result_whose_call_the_converter_never_saw_is_not_mapped()
    {
        // The converter pairs a result with its tool by call ID, from the calls it converted in this response.
        var model = new RecordedChatClient(
        [
            new ChatResponseUpdate(ChatRole.Tool, [new FunctionResultContent("call-1", Frames.Json("""{"equipmentId":"EQ-501","covered":true}"""))])
            {
                MessageId = "msg-1",
            },
        ]);
        await using var host = await CookbookHost.StartAsync(model, MapResultRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Is EQ-501 still under warranty?"));

        Assert.Equal(["RUN_STARTED", "TOOL_CALL_RESULT", "RUN_FINISHED"], frames.Types());
    }
}
