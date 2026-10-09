using System.Text.Json;
using System.Text.Json.Nodes;
using Ch20.DeclarativeUi;
using static Ch20.Tests.Surfaces;

namespace Ch20.Tests;

/// <summary>
/// The data model behind a surface: templates repeated over arrays, relative and absolute bindings, writes
/// from inputs, the catalog's check functions and the action context that goes back to the agent.
/// </summary>
public sealed class DataBindingTests
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 16, 16, 52, 7, TimeSpan.Zero);

    [Fact]
    public void A_template_repeats_over_its_array_with_each_item_as_the_scope_of_relative_bindings()
    {
        var surface = Instance(Recordings.Operations(Recordings.Wo1003));

        var checks = Node(surface, "checks").Children;

        Assert.Equal(5, checks.Count);
        Assert.All(checks, c => Assert.Equal("CheckBox", c.Type));
        Assert.Equal(new[] { "/checks/0", "/checks/1", "/checks/2", "/checks/3", "/checks/4" }, checks.Select(c => c.Scope.ToString()));
        Assert.Equal("Outdoor fan turns freely, with no bearing noise", surface.Data!.Text(checks[0]["label"], checks[0].Scope));
        Assert.False(surface.Data.Boolean(checks[0]["value"], checks[0].Scope));
        Assert.Equal("root/form/checks/check@2", checks[2].Key);
    }

    [Fact]
    public void Inputs_write_through_their_bindings_and_literals_cannot_be_written()
    {
        var surface = Instance(Recordings.Operations(Recordings.Wo1003));
        var second = Node(surface, "checks").Children[1];

        Assert.True(surface.Write(second, "value", JsonValue.Create(true)));
        Assert.True(surface.Write(Node(surface, "reading-fan_amps"), "value", JsonValue.Create("2.3")));
        Assert.False(surface.Write(Node(surface, "title"), "text", JsonValue.Create("Changed"))); // a literal belongs to the agent

        Assert.Equal(JsonValueKind.True, surface.Data!.Read(Pointer("/checks/1/passed"))!.GetValueKind());
        Assert.Equal("2.3", surface.Data.Read(Pointer("/readings/fan_amps"))!.GetValue<string>());
        Assert.Equal(2, surface.Data.Version);
        Assert.True(surface.IsTouched(second));
    }

    [Fact]
    public void Checks_use_the_catalog_functions_and_gate_the_submit_button()
    {
        var surface = Instance(Recordings.Operations(Recordings.Wo1003));
        var fan = Node(surface, "reading-fan_amps");
        var submit = Node(surface, "submit");

        Assert.Equal(new[] { "Enter the outdoor fan running current.", "Enter a number from 0 to 10 A." }, surface.FailedChecks(fan));
        Assert.Equal(new[] { "Enter every reading and choose the overall condition." }, surface.FailedChecks(submit));
        Assert.Null(surface.CreateAction(submit, Monday));

        surface.Write(fan, "value", JsonValue.Create("23"));
        Assert.Equal(new[] { "Enter a number from 0 to 10 A." }, surface.FailedChecks(fan));

        Fill(surface);
        Assert.Empty(surface.FailedChecks(Node(surface, "reading-fan_amps")));
        Assert.Empty(surface.FailedChecks(Node(surface, "submit")));
    }

    [Fact]
    public void An_action_carries_its_context_with_every_binding_resolved()
    {
        var surface = Instance(Recordings.Operations(Recordings.Wo1003));
        Fill(surface);

        var action = surface.CreateAction(Node(surface, "submit"), Monday)!;

        Assert.Equal(("submit_inspection", "inspection-WO-1003", "submit"), (action.Name, action.SurfaceId, action.SourceComponentId));
        Assert.Equal("WO-1003", action.Context["workOrderId"]!.GetValue<string>());
        Assert.Equal(new[] { false, true, true, true, true }, ((JsonArray)action.Context["checks"]!).Select(c => c!["passed"]!.GetValue<bool>()));
        Assert.Equal("10.2", action.Context["readings"]!["defrost_kohm"]!.GetValue<string>());
        Assert.Equal("follow_up", Assert.Single((JsonArray)action.Context["condition"]!)!.GetValue<string>());
        Assert.Equal("Grinding from the fan motor at start-up.", action.Context["notes"]!.GetValue<string>());
    }

    [Fact]
    public void The_action_goes_back_in_forwardedProps_in_the_shape_the_AG_UI_A2UI_middleware_reads()
    {
        var surface = Instance(Recordings.Operations(Recordings.Wo1003));
        Fill(surface);

        var forwarded = surface.CreateAction(Node(surface, "submit"), Monday)!.ToForwardedProperties();

        var outer = Assert.Single(forwarded.EnumerateObject());
        Assert.Equal("a2uiAction", outer.Name);
        var userAction = Assert.Single(outer.Value.EnumerateObject());
        Assert.Equal("userAction", userAction.Name);
        Assert.Equal(new[] { "name", "surfaceId", "sourceComponentId", "timestamp", "context" }, userAction.Value.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-11-16T16:52:07.000Z", userAction.Value.GetProperty("timestamp").GetString());

        // What the client recorded sending to the running agent has exactly this shape.
        using var request = JsonDocument.Parse(File.ReadAllText(Recordings.PathOf(Recordings.SubmitRequest)));
        var sent = request.RootElement.GetProperty("forwardedProps").GetProperty("a2uiAction").GetProperty("userAction");
        Assert.Equal(userAction.Value.GetRawText(), sent.GetRawText());
        Assert.DoesNotContain(request.RootElement.GetProperty("messages").EnumerateArray(), m => m.GetProperty("role").GetString() == "activity");
    }

    [Fact]
    public void A_surface_takes_one_action_and_then_is_read_only()
    {
        var surface = Instance(Recordings.Operations(Recordings.Wo1003));
        Fill(surface);
        var submit = Node(surface, "submit");

        surface.MarkSending();
        Assert.Null(surface.CreateAction(submit, Monday));
        Assert.False(surface.Write(Node(surface, "notes"), "value", JsonValue.Create("Too late.")));

        surface.MarkOpen();
        Assert.NotNull(surface.CreateAction(submit, Monday));
        surface.MarkSubmitted();
        Assert.Null(surface.CreateAction(submit, Monday));
    }

    [Theory]
    [InlineData("/a~1b/c~0d", true, new[] { "a/b", "c~d" })]
    [InlineData("label", false, new[] { "label" })]
    [InlineData("/", true, new string[0])]
    public void Pointers_follow_RFC_6901_plus_A2UIs_relative_paths(string path, bool absolute, string[] segments)
    {
        Assert.True(DataPointer.TryParse(path, out var pointer));
        Assert.Equal(absolute, pointer.IsAbsolute);
        Assert.Equal(segments, pointer.Segments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/a//b")]
    [InlineData("/a/")]
    [InlineData("/a~2")]
    public void Malformed_pointers_are_refused(string path) => Assert.False(DataPointer.TryParse(path, out _));

    [Fact]
    public void UpdateDataModel_upserts_at_a_path_and_removes_when_the_value_is_omitted()
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        operations.Add(new JsonObject { ["version"] = "v0.9", ["updateDataModel"] = new JsonObject { ["surfaceId"] = "inspection-WO-1003", ["path"] = "/readings/fan_amps", ["value"] = "1.4" } });
        operations.Add(new JsonObject { ["version"] = "v0.9", ["updateDataModel"] = new JsonObject { ["surfaceId"] = "inspection-WO-1003", ["path"] = "/notes" } });

        var data = Desk.Validate(operations).Surface!.Data;

        Assert.Equal("1.4", data["readings"]!["fan_amps"]!.GetValue<string>());
        Assert.False(data.ContainsKey("notes"));
    }

    [Theory]
    [InlineData("""{"name":"submit_inspection","surfaceId":"s","sourceComponentId":"submit","timestamp":"2026-11-16T16:52:07Z","context":{},"script":"x"}""", "userAction has no 'script' property.")]
    [InlineData("""{"name":"Submit Inspection","surfaceId":"s","sourceComponentId":"submit","timestamp":"2026-11-16T16:52:07Z","context":{}}""", "userAction needs a name, a surfaceId and a sourceComponentId.")]
    [InlineData("""{"name":"submit_inspection","surfaceId":"s","sourceComponentId":"submit","timestamp":"yesterday","context":{}}""", "userAction.timestamp is an ISO 8601 date and time.")]
    [InlineData("""{"name":"submit_inspection","surfaceId":"s","sourceComponentId":"submit","timestamp":"2026-11-16T16:52:07Z","context":[]}""", "userAction.context is an object of at most 16384 bytes.")]
    public void A_malformed_action_is_recognized_as_one(string userAction, string problem)
    {
        using var forwarded = JsonDocument.Parse("""{"a2uiAction":{"userAction":""" + userAction + "}}");

        var read = SurfaceUserAction.Read(forwarded.RootElement.Clone());

        Assert.True(read.IsPresent);
        Assert.Null(read.Action);
        Assert.Equal(problem, read.Problem);
    }

    [Fact]
    public void A_run_without_an_action_has_none() =>
        Assert.False(SurfaceUserAction.Read(JsonDocument.Parse("""{"other":"data"}""").RootElement).IsPresent);

    /// <summary>Fills in the HP-24S form the way Dana did: four checks passed, three readings, needs follow-up.</summary>
    internal static void Fill(SurfaceInstance surface)
    {
        foreach (var check in Node(surface, "checks").Children.Skip(1))
        {
            surface.Write(check, "value", JsonValue.Create(true));
        }

        surface.Write(Node(surface, "reading-fan_amps"), "value", JsonValue.Create("2.3"));
        surface.Write(Node(surface, "reading-defrost_kohm"), "value", JsonValue.Create("10.2"));
        surface.Write(Node(surface, "reading-suction_psig"), "value", JsonValue.Create("118"));
        surface.Write(Node(surface, "condition"), "value", new JsonArray("follow_up"));
        surface.Write(Node(surface, "notes"), "value", JsonValue.Create("Grinding from the fan motor at start-up."));
    }

    private static DataPointer Pointer(string path) => DataPointer.TryParse(path, out var pointer) ? pointer : throw new ArgumentException(path);
}
