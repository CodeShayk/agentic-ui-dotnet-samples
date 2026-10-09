using StreamOptionsCookbook;
using StreamOptionsCookbook.Tests.Infrastructure;
using Beacon.Testing;

namespace StreamOptionsCookbook.Tests;

/// <summary>Recipe 7: <c>MapStreamingToolCallArguments</c> streams a call's arguments as the model writes them.</summary>
public sealed class StreamingArgumentsTests
{
    [Fact]
    public async Task Each_OpenAI_argument_piece_becomes_its_own_TOOL_CALL_ARGS()
    {
        var model = new RecordedChatClient(
            Turns.StreamedCall("call-1", MapResultRecipe.CheckWarrantyName, """{"equip""", """mentId":"EQ""", """-501"}"""),
            Turns.Text("msg-2", "EQ-501's parts are covered until May 14, 2029."));
        await using var host = await CookbookHost.StartAsync(Pipelines.WithTools(model, MapResultRecipe.CheckWarranty), StreamingArgumentsRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Is EQ-501 still under warranty?"));
        frames.Capture("07-streaming-arguments");

        Assert.Equal(
            ["RUN_STARTED", "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_ARGS", "TOOL_CALL_ARGS", "TOOL_CALL_END", "TOOL_CALL_RESULT"],
            frames.Types().Take(7));

        var start = frames.Only("TOOL_CALL_START");
        Assert.Equal(("call-1", "check_warranty", "msg-1"), (start.Str("toolCallId"), start.Str("toolCallName"), start.Str("parentMessageId")));
        Assert.Equal("""{"equipmentId":"EQ-501"}""", string.Concat(frames.Where(f => f.Type() == "TOOL_CALL_ARGS").Select(f => f.Str("delta"))));
    }

    [Fact]
    public async Task Only_the_last_extractor_registered_counts()
    {
        var model = new RecordedChatClient(
            Turns.StreamedCall("call-1", MapResultRecipe.CheckWarrantyName, """{"equip""", """mentId":"EQ""", """-501"}"""),
            Turns.Text("msg-2", "EQ-501's parts are covered until May 14, 2029."));
        await using var host = await CookbookHost.StartAsync(
            Pipelines.WithTools(model, MapResultRecipe.CheckWarranty),
            options => StreamingArgumentsRecipe.Apply(options).MapStreamingToolCallArguments(_ => null));

        var frames = await host.RunAsync(Turns.Run("Is EQ-501 still under warranty?"));

        // The second extractor replaced the first and finds nothing, so the call goes out whole.
        var args = frames.Only("TOOL_CALL_ARGS");
        Assert.Equal("""{"equipmentId":"EQ-501"}""", args.Str("delta"));
    }
}
