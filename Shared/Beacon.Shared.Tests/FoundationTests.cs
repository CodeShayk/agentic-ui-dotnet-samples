using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Beacon.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Shared.Tests;

public sealed class FoundationTests
{
    private static ScriptedChatClient Scripted() => new(options: new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero });

    [Fact]
    public async Task Scripted_model_calls_get_work_order_when_the_tool_is_offered()
    {
        var tools = new BeaconTools(new InMemoryFieldServiceStore()).CreateReadOnlyTools();
        var response = await Scripted().GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What's the status of WO-1001?")],
            new ChatOptions { Tools = tools },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("get_work_order", call.Name);
        Assert.Equal("WO-1001", call.Arguments!["workOrderId"]);
    }

    [Fact]
    public async Task Scripted_model_with_function_invocation_answers_from_the_tool_result()
    {
        IChatClient client = new ChatClientBuilder(Scripted()).UseFunctionInvocation().Build();
        var tools = new BeaconTools(new InMemoryFieldServiceStore()).CreateReadOnlyTools();

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What's the status of WO-1001?")],
            new ChatOptions { Tools = tools },
            TestContext.Current.CancellationToken);

        Assert.Contains("WO-1001", response.Text);
        Assert.Contains("Harbor View Dental", response.Text);
    }

    [Fact]
    public async Task Endpoint_streams_a_well_formed_run_over_SSE()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAGUIHosting();
        builder.Services.AddChatClient(new RecordedChatClient(RecordedChatClient.TextTurn("msg-1", "Hello ", "from Beacon.")));

        await using var app = builder.Build();
        app.MapAGUIEndpoint("/agent");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var events = await AGUIWire.RunAsync(app.GetTestClient(), "/agent", AGUIWire.UserRun("Hi"), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED"],
            AGUIWire.Types(events));

        var started = AGUIWire.ToWireJson(events[0]);
        Assert.Equal("thread-1", started.GetProperty("threadId").GetString());
    }

    [Fact]
    public async Task Endpoint_never_writes_null_for_optional_fields()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAGUIHosting();
        builder.Services.AddChatClient(new RecordedChatClient(RecordedChatClient.TextTurn("msg-1", "Hello.")));

        await using var app = builder.Build();
        app.MapAGUIEndpoint("/agent");
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Read the raw frames: round-tripping through the SDK would hide nulls.
        var frames = await AGUIWire.RunRawAsync(app.GetTestClient(), "/agent", AGUIWire.UserRun("Hi"), TestContext.Current.CancellationToken);

        Assert.NotEmpty(frames);
        Assert.All(frames, frame => Assert.Empty(AGUIWire.NullProperties(frame)));
    }
}
