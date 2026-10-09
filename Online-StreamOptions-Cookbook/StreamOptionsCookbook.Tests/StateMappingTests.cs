using System.Text.Json;
using StreamOptionsCookbook;
using StreamOptionsCookbook.Tests.Infrastructure;
using Beacon.Testing;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook.Tests;

/// <summary>Recipes 5 and 6: <c>MapResultAsStateSnapshot</c> and <c>MapResultAsStateDelta</c>.</summary>
public sealed class StateMappingTests
{
    private const string Sheet = """
        {"workOrderId":"WO-1001","problem":"Air conditioner short-cycling and blowing warm air","checklist":[
          {"id":"C1","text":"Check thermostat settings and wiring","done":true},
          {"id":"C2","text":"Measure refrigerant pressures","done":false},
          {"id":"C3","text":"Inspect run capacitor","done":false},
          {"id":"C4","text":"Test compressor amperage","done":false}]}
        """;

    [Fact]
    public async Task A_tool_result_becomes_the_state_snapshot()
    {
        var model = new RecordedChatClient(
            Turns.Call("call-1", StateSnapshotRecipe.OpenWorkOrderSheetName, """{"workOrderId":"WO-1001"}"""),
            Turns.Text("msg-2", "WO-1001 is on the sheet."));
        await using var host = await CookbookHost.StartAsync(Pipelines.WithTools(model, StateSnapshotRecipe.OpenWorkOrderSheet), StateSnapshotRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Open WO-1001.", stateJson: "{}"));
        frames.Capture("05-state-snapshot");

        // Chapter 12's name for the tool that opens a work order on the shared sheet.
        Assert.Equal("open_work_order_sheet", frames.Only("TOOL_CALL_START").Str("toolCallName"));

        var result = frames.IndexOf("TOOL_CALL_RESULT");
        Assert.Equal("STATE_SNAPSHOT", frames[result + 1].Type());

        // The result is both what the model reads and the new state, so the bytes go out twice.
        var content = frames[result].Str("content")!;
        Assert.True(Frames.Json(content).SameJson(frames[result + 1].GetProperty("snapshot")));
        Assert.DoesNotContain('\n', content); // compact tool options
        Assert.Equal("C3", frames[result + 1].GetProperty("snapshot").GetProperty("checklist")[2].Str("id"));
    }

    [Fact]
    public async Task A_work_order_the_tool_cannot_open_leaves_the_sheet_as_the_client_sent_it()
    {
        // The result is the new state, so the tool answers an unknown work order with the client's own sheet.
        var model = new RecordedChatClient(
            Turns.Call("call-1", StateSnapshotRecipe.OpenWorkOrderSheetName, """{"workOrderId":"WO-9999"}"""),
            Turns.Text("msg-2", "I couldn't find WO-9999."));
        await using var host = await CookbookHost.StartAsync(Pipelines.WithTools(model, StateSnapshotRecipe.OpenWorkOrderSheet), StateSnapshotRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Open WO-9999.", stateJson: Sheet));

        Assert.True(Frames.Json(Sheet).SameJson(frames.Only("STATE_SNAPSHOT").GetProperty("snapshot")));
    }

    [Fact]
    public async Task A_typed_return_value_maps_because_AIFunctionFactory_serializes_it_but_default_options_indent_it()
    {
        // AIFunctionFactory turns any return value into a JsonElement, so a record works; with the default
        // options that JsonElement is written indented, and TOOL_CALL_RESULT carries the line breaks.
        var typed = AIFunctionFactory.Create(
            (string workOrderId) => new WorkSheet(workOrderId, "Air conditioner short-cycling", [new("C1", "Check thermostat", false)]),
            StateSnapshotRecipe.OpenWorkOrderSheetName);
        var model = new RecordedChatClient(
            Turns.Call("call-1", StateSnapshotRecipe.OpenWorkOrderSheetName, """{"workOrderId":"WO-1001"}"""),
            Turns.Text("msg-2", "WO-1001 is on the sheet."));
        await using var host = await CookbookHost.StartAsync(Pipelines.WithTools(model, typed), StateSnapshotRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Open WO-1001.", stateJson: "{}"));

        Assert.Equal("WO-1001", frames.Only("STATE_SNAPSHOT").GetProperty("snapshot").Str("workOrderId"));
        Assert.Contains('\n', frames.Only("TOOL_CALL_RESULT").Str("content")!);
    }

    [Fact]
    public async Task A_mapped_tool_that_throws_ends_the_run_in_RUN_ERROR()
    {
        // The failure reaches the converter as the string "Error: Function failed.", not a JsonElement,
        // so the built-in state mapper throws and the run ends. The model never gets its next turn.
        var failing = AIFunctionFactory.Create(
            JsonElement (string workOrderId) => throw new InvalidOperationException("Work order store unavailable."),
            StateSnapshotRecipe.OpenWorkOrderSheetName);
        var model = new RecordedChatClient(
            Turns.Call("call-1", StateSnapshotRecipe.OpenWorkOrderSheetName, """{"workOrderId":"WO-1001"}"""),
            Turns.Text("msg-2", "I couldn't open WO-1001."));
        await using var host = await CookbookHost.StartAsync(Pipelines.WithTools(model, failing), StateSnapshotRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Open WO-1001.", stateJson: "{}"));
        frames.Capture("05-state-snapshot-failure");

        Assert.Equal(["RUN_STARTED", "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "TOOL_CALL_RESULT", "RUN_ERROR"], frames.Types());
        Assert.Equal("Error: Function failed.", frames.Only("TOOL_CALL_RESULT").Str("content"));
        Assert.Equal("StreamingError", frames[^1].Str("code"));
        Assert.Single(model.Calls);
    }

    [Fact]
    public async Task A_patch_result_becomes_a_state_delta_against_the_clients_state()
    {
        var model = new RecordedChatClient(
            Turns.Call("call-1", StateDeltaRecipe.SetChecklistItemName, """{"itemId":"C3","done":true}"""),
            Turns.Text("msg-2", "C3 is ticked."));
        await using var host = await CookbookHost.StartAsync(Pipelines.WithTools(model, StateDeltaRecipe.SetChecklistItem), StateDeltaRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Tick C3 on WO-1001.", stateJson: Sheet));
        frames.Capture("06-state-delta");

        var result = frames.IndexOf("TOOL_CALL_RESULT");
        Assert.Equal("STATE_DELTA", frames[result + 1].Type());
        Assert.True(Frames.Json("""[{"op":"test","path":"/checklist/2/id","value":"C3"},{"op":"replace","path":"/checklist/2/done","value":true}]""")
            .SameJson(frames[result + 1].GetProperty("delta")));
    }

    [Fact]
    public async Task An_item_that_is_not_on_the_sheet_sends_an_empty_patch()
    {
        var model = new RecordedChatClient(
            Turns.Call("call-1", StateDeltaRecipe.SetChecklistItemName, """{"itemId":"C9","done":true}"""),
            Turns.Text("msg-2", "There is no C9 on this checklist."));
        await using var host = await CookbookHost.StartAsync(Pipelines.WithTools(model, StateDeltaRecipe.SetChecklistItem), StateDeltaRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Tick C9 on WO-1001.", stateJson: Sheet));

        Assert.Equal(JsonValueKind.Array, frames.Only("STATE_DELTA").GetProperty("delta").ValueKind);
        Assert.Equal(0, frames.Only("STATE_DELTA").GetProperty("delta").GetArrayLength());
    }

    [Fact]
    public async Task The_converter_does_not_check_that_a_delta_is_a_patch()
    {
        var careless = AIFunctionFactory.Create(
            (string itemId, bool done) => Frames.Json("""{"error":"Unknown checklist item."}"""),
            StateDeltaRecipe.SetChecklistItemName);
        var model = new RecordedChatClient(
            Turns.Call("call-1", StateDeltaRecipe.SetChecklistItemName, """{"itemId":"C9","done":true}"""),
            Turns.Text("msg-2", "There is no C9 on this checklist."));
        await using var host = await CookbookHost.StartAsync(Pipelines.WithTools(model, careless), StateDeltaRecipe.Apply);

        var frames = await host.RunAsync(Turns.Run("Tick C9 on WO-1001.", stateJson: Sheet));

        // An object where a JSON Patch array belongs: the spec makes this fatal for the client.
        Assert.Equal(JsonValueKind.Object, frames.Only("STATE_DELTA").GetProperty("delta").ValueKind);
    }
}
