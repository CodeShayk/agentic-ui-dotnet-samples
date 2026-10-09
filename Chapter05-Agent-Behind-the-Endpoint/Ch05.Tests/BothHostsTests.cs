using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Testing;
using Microsoft.Extensions.AI;

namespace Ch05.Tests;

/// <summary>The same request, the same client, two hosts: the plain pipeline and Agent Framework.</summary>
public sealed class BothHostsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HostKind.Plain)]
    [InlineData(HostKind.AgentFramework)]
    public async Task Both_hosts_stream_the_same_well_formed_run(HostKind kind)
    {
        await using var host = AgentHosts.Start(kind);

        var events = await AGUIWire.RunAsync(
            host.CreateClient(), "/agent", AGUIWire.UserRun("What is the status of WO-1001?"), Ct);

        Wire.AssertWellFormed(events, "thread-1", "run-1");
        Assert.Equal(
            [
                "RUN_STARTED",
                "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "TOOL_CALL_RESULT",
                "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END",
                "RUN_FINISHED",
            ],
            Wire.Shape(events));
    }

    [Fact]
    public async Task One_client_gets_the_same_answer_from_either_host()
    {
        List<string> answers = [];
        foreach (var kind in Enum.GetValues<HostKind>())
        {
            await using var host = AgentHosts.Start(kind);
            using IChatClient beacon = new AGUIChatClient(new AGUIChatClientOptions(host.CreateClient(), "/agent"));

            var response = await beacon.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "What is the status of WO-1001?") { MessageId = "user-1" }],
                new ChatOptions(),
                Ct);

            answers.Add(response.Text);
        }

        Assert.Equal(answers[0], answers[1]);
        Assert.Contains("Harbor View Dental", answers[0]);
    }

    [Theory]
    [InlineData(HostKind.Plain)]
    [InlineData(HostKind.AgentFramework)]
    public async Task Unset_optional_fields_are_omitted_rather_than_null(HostKind kind)
    {
        await using var host = AgentHosts.Start(kind);

        var frames = await Wire.RawFramesAsync(host.CreateClient(), """
            {"threadId":"thread-1","runId":"run-1","messages":[{"id":"user-1","role":"user","content":"Hello"}]}
            """, Ct);

        // AG-UI 1.0 forbids "parentRunId": null; the Agent Framework host needs its JSON fix for this.
        Assert.All(frames, frame =>
            Assert.All(frame.EnumerateObject(), property =>
                Assert.NotEqual(JsonValueKind.Null, property.Value.ValueKind)));
        Assert.False(frames[0].TryGetProperty("parentRunId", out _));
        Assert.False(frames[^1].TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Agent_Framework_names_the_agent_on_its_text_messages()
    {
        List<TextMessageStartEvent> starts = [];
        foreach (var kind in Enum.GetValues<HostKind>())
        {
            await using var host = AgentHosts.Start(kind);
            var events = await AGUIWire.RunAsync(host.CreateClient(), "/agent", AGUIWire.UserRun("Hello"), Ct);
            starts.Add(events.OfType<TextMessageStartEvent>().Single());
        }

        // The one visible difference between the hosts: the agent's name rides on TEXT_MESSAGE_START.
        Assert.Null(starts[0].Name);
        Assert.Equal(global::Ch05.Beacon.AgentFramework.Program.AgentName, starts[1].Name);
    }

    [Theory]
    [InlineData(HostKind.Plain)]
    [InlineData(HostKind.AgentFramework)]
    public async Task Server_tools_run_inside_the_run_through_function_invocation(HostKind kind)
    {
        await using var host = AgentHosts.Start(kind);

        var events = await AGUIWire.RunAsync(
            host.CreateClient(), "/agent", AGUIWire.UserRun("What is the status of WO-1001?"), Ct);

        var start = events.OfType<ToolCallStartEvent>().Single();
        Assert.Equal("get_work_order", start.ToolCallName);

        var args = events.OfType<ToolCallArgsEvent>().Where(e => e.ToolCallId == start.ToolCallId);
        using var arguments = JsonDocument.Parse(string.Concat(args.Select(a => a.Delta)));
        Assert.Equal("WO-1001", arguments.RootElement.GetProperty("workOrderId").GetString());

        // The server ran the tool: its result is on the wire, and the answer is built from it.
        var result = AGUIWire.ToWireJson(events.OfType<ToolCallResultEvent>().Single());
        Assert.Equal(start.ToolCallId, result.GetProperty("toolCallId").GetString());
        Assert.Contains("Harbor View Dental", result.GetProperty("content").GetString());
        Assert.Contains("Harbor View Dental", Wire.Text(events));
    }
}
