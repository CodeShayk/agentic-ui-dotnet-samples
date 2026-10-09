using StreamOptionsCookbook;
using StreamOptionsCookbook.Tests.Infrastructure;
using Beacon.Testing;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook.Tests;

/// <summary>Recipe 3: <c>MapCall</c> sends what a tool is about to do before it runs.</summary>
public sealed class MapCallTests
{
    private const string OrderArguments = """{"workOrderId":"WO-1001","sku":"CAP-45-5","quantity":1}""";

    [Fact]
    public async Task A_complete_call_becomes_a_predictive_state_delta_between_its_end_and_its_result()
    {
        var model = new RecordedChatClient(
            Turns.Call("call-1", MapCallRecipe.PlacePartOrderName, OrderArguments),
            Turns.Text("msg-2", "Ordered one CAP-45-5 for WO-1001."));
        await using var host = await CookbookHost.StartAsync(Pipelines.WithTools(model, MapCallRecipe.PlacePartOrder), MapCallRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Order one CAP-45-5 for WO-1001.", stateJson: "{}"));
        frames.Capture("03-map-call");

        Assert.Equal(
            ["RUN_STARTED", "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "STATE_DELTA", "TOOL_CALL_RESULT",
             "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED"],
            frames.Types());

        var delta = frames.Only("STATE_DELTA");
        Assert.True(Frames.Json("""[{"op":"add","path":"/pendingOrder","value":{"sku":"CAP-45-5","quantity":1,"status":"placing"}}]""")
            .SameJson(delta.GetProperty("delta")));
        Assert.False(delta.HasRawEvent());
    }

    [Fact]
    public async Task With_streamed_arguments_the_mapper_still_runs_once_after_TOOL_CALL_END()
    {
        var model = new RecordedChatClient(
            Turns.StreamedCall("call-1", MapCallRecipe.PlacePartOrderName, "{\"workOrd", "erId\":\"WO-1001\",", "\"sku\":\"CAP-45-5\",", "\"quantity\":1}"),
            Turns.Text("msg-2", "Ordered one CAP-45-5 for WO-1001."));
        await using var host = await CookbookHost.StartAsync(
            Pipelines.WithTools(model, MapCallRecipe.PlacePartOrder),
            options => MapCallRecipe.Apply(StreamingArgumentsRecipe.Apply(options)));

        var frames = await host.RunAsync(Turns.Run("Order one CAP-45-5 for WO-1001.", stateJson: "{}"));

        // Four argument pieces, then the end, then the one mapped delta. MapCall never sees a partial call.
        Assert.Equal(
            ["RUN_STARTED", "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_ARGS", "TOOL_CALL_ARGS", "TOOL_CALL_ARGS",
             "TOOL_CALL_END", "STATE_DELTA", "TOOL_CALL_RESULT"],
            frames.Types().Take(9));
    }

    [Fact]
    public async Task A_call_that_waits_for_approval_is_not_mapped()
    {
        var model = new RecordedChatClient(Turns.Call("call-1", MapCallRecipe.PlacePartOrderName, OrderArguments));
        await using var host = await CookbookHost.StartAsync(
            Pipelines.WithTools(model, new ApprovalRequiredAIFunction(MapCallRecipe.PlacePartOrder)),
            MapCallRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Order one CAP-45-5 for WO-1001.", stateJson: "{}"));

        Assert.Equal(["RUN_STARTED", "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "RUN_FINISHED"], frames.Types());
        Assert.Equal("interrupt", frames[^1].GetProperty("outcome").Str("type"));
    }
}
