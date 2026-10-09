using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Testing;
using Ch20.DeclarativeUi;
using static Ch20.Tests.Frame;

namespace Ch20.Tests;

/// <summary>
/// The surface on the wire, from the in-process agent and from the curl -N captures, against the binding
/// verified in the AG-UI repository on 2026-10-03: an <c>ACTIVITY_SNAPSHOT</c> with <c>activityType</c>
/// <c>a2ui-surface</c>, the A2UI v0.9 operations under <c>a2ui_operations</c>, an ID derived from the tool
/// call and <c>replace: true</c>, right after the tool's own <c>TOOL_CALL_RESULT</c>.
/// </summary>
public sealed class WireShapeTests(AgentHost agent) : IClassFixture<AgentHost>
{
    [Fact]
    public async Task A_surface_travels_as_an_a2ui_surface_activity_right_after_its_tool_result()
    {
        var frames = await agent.RunRawAsync("Start the inspection checklist for WO-1003.", threadId: "t-wire-1");

        AssertSurfaceShape(frames, "inspection-WO-1003");
        Assert.Empty(frames.SelectMany(f => AGUIWire.NullProperties(f)));
    }

    [Theory]
    [InlineData(Recordings.Wo1003, "inspection-WO-1003")]
    [InlineData(Recordings.Wo1001, "inspection-WO-1001")]
    [InlineData(Recordings.Wo1005, "inspection-WO-1005")]
    public void The_curl_captures_have_the_same_shape(string recording, string surfaceId) =>
        AssertSurfaceShape(Recordings.Frames(recording), surfaceId);

    [Fact]
    public async Task A_malformed_first_attempt_is_answered_with_A2UI_validation_errors_and_never_painted()
    {
        var frames = await agent.RunRawAsync("Start the inspection checklist for WO-1001.", threadId: "t-wire-2");

        var renders = frames.Where(f => Type(f) == "TOOL_CALL_START" && f.GetProperty("toolCallName").GetString() == A2ui.RenderToolName)
            .Select(f => f.GetProperty("toolCallId").GetString()!).ToList();
        Assert.Equal(2, renders.Count);

        using var first = JsonDocument.Parse(ResultOf(frames, renders[0]));
        Assert.Equal("rejected", first.RootElement.GetProperty("status").GetString());
        var error = Assert.Single(first.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("VALIDATION_FAILED", error.GetProperty("code").GetString());
        Assert.Equal("inspection-WO-1001", error.GetProperty("surfaceId").GetString());
        Assert.Equal("/components/13/child", error.GetProperty("path").GetString()); // a path into the model's own arguments
        Assert.StartsWith("unresolved_child:", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        using var second = JsonDocument.Parse(ResultOf(frames, renders[1]));
        Assert.Equal("accepted", second.RootElement.GetProperty("status").GetString());

        var painted = Assert.Single(frames, f => Type(f) == "ACTIVITY_SNAPSHOT");
        Assert.Equal(A2ui.ActivityMessageId(renders[1]), painted.GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task A_client_tool_named_render_a2ui_cannot_replace_the_servers()
    {
        var input = AGUIWire.UserRun("Start the inspection checklist for WO-1003.", "t-wire-3", "r-1");
        using var permissive = JsonDocument.Parse("""{"type":"object","additionalProperties":true}""");
        input.Tools = [new AGUITool { Name = A2ui.RenderToolName, Description = "Render anything.", Parameters = permissive.RootElement.Clone() }];

        var frames = await AGUIWire.RunRawAsync(agent.CreateClient(), "/agent", input, TestContext.Current.CancellationToken);

        // The server's tool answered (a client tool would have ended the run unanswered), and its surface was painted.
        AssertSurfaceShape(frames, "inspection-WO-1003");
        Assert.Equal("success", frames[^1].GetProperty("outcome").GetProperty("type").GetString());
    }

    private static void AssertSurfaceShape(IReadOnlyList<JsonElement> frames, string surfaceId)
    {
        Assert.Equal("RUN_STARTED", Type(frames[0]));
        Assert.Equal(AGUIProtocol.Version, frames[0].GetProperty("protocolVersion").GetString()); // declared since Chapter 19
        Assert.Equal("RUN_FINISHED", Type(frames[^1]));

        var index = frames.ToList().FindIndex(f => Type(f) == "ACTIVITY_SNAPSHOT");
        var activity = frames[index];
        var result = frames[index - 1];
        Assert.Equal("TOOL_CALL_RESULT", Type(result));
        var callId = result.GetProperty("toolCallId").GetString()!;
        Assert.Equal(A2ui.RenderToolName, frames.Single(f => Type(f) == "TOOL_CALL_START" && f.GetProperty("toolCallId").GetString() == callId).GetProperty("toolCallName").GetString());

        // The activity: its ID from the tool call, the A2UI activity type, replace, and nothing else in content.
        Assert.Equal(new[] { "type", "messageId", "activityType", "content", "replace" }, activity.EnumerateObject().Select(p => p.Name));
        Assert.Equal($"a2ui-surface-{callId}", activity.GetProperty("messageId").GetString());
        Assert.Equal("a2ui-surface", activity.GetProperty("activityType").GetString());
        Assert.True(activity.GetProperty("replace").GetBoolean());
        var content = Assert.Single(activity.GetProperty("content").EnumerateObject());
        Assert.Equal("a2ui_operations", content.Name);

        // The operations: A2UI v0.9 messages, each a version and exactly one operation, in order.
        var operations = content.Value.EnumerateArray().ToList();
        Assert.Equal(new[] { "createSurface", "updateComponents", "updateDataModel" }, operations.Select(o => o.EnumerateObject().Single(p => p.Name != "version").Name));
        Assert.All(operations, o =>
        {
            Assert.Equal(2, o.EnumerateObject().Count());
            Assert.Equal("v0.9", o.GetProperty("version").GetString());
        });

        var create = operations[0].GetProperty("createSurface");
        Assert.Equal(surfaceId, create.GetProperty("surfaceId").GetString());
        Assert.Equal(BeaconCatalog.Id, create.GetProperty("catalogId").GetString());
        Assert.Equal(surfaceId, operations[1].GetProperty("updateComponents").GetProperty("surfaceId").GetString());
        Assert.Contains(operations[1].GetProperty("updateComponents").GetProperty("components").EnumerateArray(), c => c.GetProperty("id").GetString() == "root");
        Assert.Equal("/", operations[2].GetProperty("updateDataModel").GetProperty("path").GetString());

        // And it passes the Desk's own validation as it arrived.
        Assert.NotEqual(SurfaceOutcome.Rejected, Surfaces.Desk.Validate(System.Text.Json.Nodes.JsonNode.Parse(content.Value.GetRawText())).Outcome);
    }

    private static string ResultOf(IReadOnlyList<JsonElement> frames, string callId) =>
        frames.Single(f => Type(f) == "TOOL_CALL_RESULT" && f.GetProperty("toolCallId").GetString() == callId).GetProperty("content").GetString()!;
}
