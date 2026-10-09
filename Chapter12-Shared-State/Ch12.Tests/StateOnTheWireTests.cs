using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.Testing;

namespace Ch12.Tests;

/// <summary>What Ch12.Beacon.Agent puts on the wire, read from the raw SSE frames.</summary>
public sealed class StateOnTheWireTests
{
    private static readonly CancellationToken Cancel = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Opening_a_work_order_sends_its_sheet_as_a_snapshot_right_after_the_tool_result()
    {
        await using var agent = new AgentHost();
        var frames = await AGUIWire.RunRawAsync(agent.CreateClient(), "/agent", Runs.Input("Open WO-1001"), Cancel);
        var types = frames.Select(Runs.Type).ToList();

        var result = types.IndexOf("TOOL_CALL_RESULT");
        Assert.Equal("STATE_SNAPSHOT", types[result + 1]);
        Assert.Single(types, "STATE_SNAPSHOT"); // no settling snapshot: the run sent no deltas

        var snapshot = frames[result + 1].GetProperty("snapshot");
        Assert.Equal(2, snapshot.GetProperty("schemaVersion").GetInt32());
        var sheet = snapshot.GetProperty("workOrder");
        Assert.Equal("WO-1001", sheet.GetProperty("id").GetString());
        Assert.Equal("Harbor View Dental", sheet.GetProperty("customer").GetString());
        Assert.Equal("Contoso Air CA-36X", sheet.GetProperty("equipment").GetString());
        Assert.Equal("High", sheet.GetProperty("priority").GetString());
        Assert.Equal("Priya Raman", sheet.GetProperty("technician").GetString());
        Assert.Equal("Scheduled", sheet.GetProperty("status").GetString());
        Assert.Equal(["C1", "C2", "C3", "C4"], sheet.GetProperty("checklist").EnumerateArray().Select(i => i.GetProperty("id").GetString()));

        // The tool result the model reads is the same document.
        Assert.True(JsonNode.DeepEquals(Runs.Node(snapshot), JsonNode.Parse(frames[result].GetProperty("content").GetString()!)));
        Assert.All(frames, f => Assert.Empty(AGUIWire.NullProperties(f)));
    }

    [Fact]
    public async Task Shared_state_carries_only_what_the_sheet_shows()
    {
        await using var agent = new AgentHost();
        var frames = await AGUIWire.RunRawAsync(agent.CreateClient(), "/agent", Runs.Input("Open WO-1001"), Cancel);
        var snapshot = frames.Single(f => Runs.Type(f) == "STATE_SNAPSHOT").GetProperty("snapshot");

        Assert.Equal(["schemaVersion", "workOrder"], snapshot.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["id", "customer", "equipment", "summary", "priority", "technician", "status", "checklist", "notes"],
            snapshot.GetProperty("workOrder").EnumerateObject().Select(p => p.Name));

        // The customer's phone and address, the site's coordinates, the unit's serial number: all in the store, none in state.
        var text = snapshot.GetRawText();
        Assert.DoesNotContain("555-0141", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Wharf", text, StringComparison.Ordinal);
        Assert.DoesNotContain("43.0718", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CA36X-2219", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_change_is_a_delta_after_its_result_and_the_run_settles_with_a_snapshot_that_equals_them()
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient();
        var sheet = await Runs.OpenSheetAsync(http);

        var frames = await AGUIWire.RunRawAsync(http, "/agent",
            Runs.Input("Priya measured the run capacitor on WO-1001: it reads 38 µF against its 45 µF rating, 15.6% low.", sheet, runId: "r-2"), Cancel);
        var types = frames.Select(Runs.Type).ToList();

        var deltas = frames.Where(f => Runs.Type(f) == "STATE_DELTA").Select(f => f.GetProperty("delta")).ToList();
        Assert.Equal(3, deltas.Count);
        Assert.All(Enumerable.Range(0, types.Count).Where(i => types[i] == "STATE_DELTA"), i => Assert.Equal("TOOL_CALL_RESULT", types[i - 1]));

        // The first pins the item it changes, then changes it.
        Assert.Equal(
            """[{"op":"test","path":"/workOrder/checklist/2/id","value":"C3"},{"op":"replace","path":"/workOrder/checklist/2/done","value":true}]""",
            deltas[0].GetRawText());
        Assert.Equal("/workOrder/checklist/-", deltas[1][0].GetProperty("path").GetString());
        Assert.Equal("B1", deltas[1][0].GetProperty("value").GetProperty("id").GetString());
        Assert.Equal("/workOrder/notes/-", deltas[2][0].GetProperty("path").GetString());
        Assert.Equal("Run capacitor reads 15.6% below its rating. Replace it before testing the compressor.", deltas[2][0].GetProperty("value").GetString());

        // The settling snapshot is the last state event, and it is exactly the input state plus the deltas.
        Assert.Equal("STATE_SNAPSHOT", types.Last(t => t.StartsWith("STATE_", StringComparison.Ordinal)));
        var expected = deltas.Aggregate((JsonNode?)sheet, Runs.Apply);
        var settled = Runs.Node(frames.Last(f => Runs.Type(f) == "STATE_SNAPSHOT").GetProperty("snapshot"));
        Assert.True(JsonNode.DeepEquals(expected, settled), settled.ToJsonString());
        Assert.Equal("RUN_FINISHED", types[^1]);
    }

    [Fact]
    public async Task Fields_the_server_owns_are_repaired_and_announced_before_anything_else()
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient();
        var sheet = await Runs.OpenSheetAsync(http);
        sheet["workOrder"]!["priority"] = "Low";
        sheet["workOrder"]!["customer"] = "Someone Else";
        sheet["workOrder"]!["checklist"]![0]!["done"] = true; // shared: the client's to keep

        var frames = await AGUIWire.RunRawAsync(http, "/agent", Runs.Input("What is the status of WO-1001?", sheet, runId: "r-2"), Cancel);

        Assert.Equal(["RUN_STARTED", "STATE_SNAPSHOT"], frames.Take(2).Select(Runs.Type));
        var repaired = frames[1].GetProperty("snapshot").GetProperty("workOrder");
        Assert.Equal("High", repaired.GetProperty("priority").GetString());
        Assert.Equal("Harbor View Dental", repaired.GetProperty("customer").GetString());
        Assert.True(repaired.GetProperty("checklist")[0].GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task A_version_1_sheet_is_upgraded_and_announced()
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient();
        var sheet = await Runs.OpenSheetAsync(http);
        sheet.AsObject().Remove("schemaVersion");                 // version 1 didn't write it
        sheet["workOrder"]!.AsObject().Remove("notes");             // and had no notes

        var frames = await AGUIWire.RunRawAsync(http, "/agent", Runs.Input("What is the status of WO-1001?", sheet, runId: "r-2"), Cancel);

        var upgraded = frames[1].GetProperty("snapshot");
        Assert.Equal(2, upgraded.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, upgraded.GetProperty("workOrder").GetProperty("notes").GetArrayLength());
    }

    [Fact]
    public async Task A_sheet_that_needs_no_repair_gets_no_snapshot_and_a_run_that_changes_nothing_sends_no_state()
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient();
        var sheet = await Runs.OpenSheetAsync(http);

        var frames = await AGUIWire.RunRawAsync(http, "/agent", Runs.Input("What is the status of WO-1001?", sheet, runId: "r-2"), Cancel);

        Assert.DoesNotContain(frames, f => Runs.Type(f).StartsWith("STATE_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2026-11-17", "Return visit on Tuesday, November 17 to confirm the repair")]
    [InlineData(null, "Return visit on a day to be agreed to confirm the repair")]
    public async Task Context_informs_the_change_but_only_state_carries_it(string? boardDay, string expected)
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient();
        var sheet = await Runs.OpenSheetAsync(http);
        IList<AGUIContext> context = boardDay is null ? [] : [new AGUIContext { Description = "The day selected on the dispatcher's board (ISO 8601 date)", Value = boardDay }];

        var frames = await AGUIWire.RunRawAsync(http, "/agent",
            Runs.Input("Add a return visit on the day selected on my board.", sheet, runId: "r-2", context: context), Cancel);

        var delta = frames.Single(f => Runs.Type(f) == "STATE_DELTA").GetProperty("delta");
        Assert.Equal(expected, delta[0].GetProperty("value").GetProperty("text").GetString());

        // The date itself never becomes state: context is read-only input for the model.
        var settled = frames.Last(f => Runs.Type(f) == "STATE_SNAPSHOT").GetProperty("snapshot").GetRawText();
        Assert.DoesNotContain("2026-11-17", settled, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_published_schema_is_closed()
    {
        await using var agent = new AgentHost();
        var schema = JsonDocument.Parse(await agent.CreateClient().GetStringAsync("/agent/state-schema", Cancel)).RootElement;

        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var sheet = schema.GetProperty("properties").GetProperty("workOrder");
        Assert.False(sheet.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("checklist", sheet.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Contains("Scheduled", sheet.GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }
}
