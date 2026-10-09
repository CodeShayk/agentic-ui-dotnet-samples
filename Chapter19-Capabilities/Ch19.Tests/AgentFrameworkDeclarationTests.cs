using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Testing;
using Ch19.Beacon.Agent.Versioning;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch19.Tests;

/// <summary>
/// Agent Framework's MapAGUIServer (preview) has no option for declaring the protocol version. These tests
/// show what a client sees without one, and that a RUN_STARTED supplied by the agent's own pipeline reaches
/// the wire, once per run, even when the run calls a tool.
/// </summary>
public sealed class AgentFrameworkDeclarationTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_declaration_layer_at_the_top_of_the_agents_pipeline_stamps_RUN_STARTED_behind_MapAGUIServer(bool declare)
    {
        await using var app = await StartAsync(declare);

        var frames = await AGUIWire.RunRawAsync(app.GetTestClient(), "/agent", AGUIWire.UserRun("What's Priya's next free slot?", "t-af", "r-1"), Cancel);

        // One RUN_STARTED, first, though function invocation called the model twice.
        var started = Assert.Single(frames, f => Type(f) == "RUN_STARTED");
        Assert.Equal(0, frames.ToList().IndexOf(started));
        Assert.Contains(frames, f => Type(f) == "TOOL_CALL_RESULT");
        Assert.Equal("RUN_FINISHED", Type(frames[^1]));
        Assert.Equal(("t-af", "r-1"), (started.GetProperty("threadId").GetString(), started.GetProperty("runId").GetString()));

        // Without the layer the stream says nothing about its version, which a client must read as an older peer.
        Assert.Equal(declare ? "1.0" : null, started.TryGetProperty("protocolVersion", out var version) ? version.GetString() : null);
    }

    private static async Task<WebApplication> StartAsync(bool declare)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAGUIServer();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver));
        var app = builder.Build();

        // A tool call, then the answer. The declaration goes on top, above function invocation.
        var model = new RecordedChatClient(
            [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", "get_schedule", new Dictionary<string, object?>())]) { MessageId = "msg-1" }],
            RecordedChatClient.TextTurn("msg-2", "Priya is free at 1 p.m."));
        var pipeline = new ChatClientBuilder(model);
        if (declare)
        {
            pipeline.Use(inner => new RunStartedDeclarationChatClient(inner));
        }

        var agent = pipeline.UseFunctionInvocation().Build().AsAIAgent(
            instructions: "Answer scheduling questions.",
            name: "beacon",
            tools: [AIFunctionFactory.Create(() => "TECH-1 is free from 13:00.", "get_schedule", "Gets today's schedule.")]);

        app.MapAGUIServer("/agent", agent);
        await app.StartAsync(Cancel);
        return app;
    }

    private static string Type(JsonElement frame) => frame.GetProperty("type").GetString()!;
}
