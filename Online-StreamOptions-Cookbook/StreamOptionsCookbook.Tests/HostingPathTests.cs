using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using StreamOptionsCookbook;
using StreamOptionsCookbook.Tests.Infrastructure;
using Beacon.Testing;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace StreamOptionsCookbook.Tests;

/// <summary>
/// Supplying the options on both hosting paths. The book's <c>MapAGUIEndpoint</c> takes a factory and calls it per
/// request; Agent Framework's <c>MapAGUIServer</c> (1.23.0-preview.260928.1) takes no options argument and reads
/// them from endpoint metadata, or else from <c>IOptions&lt;AGUIStreamOptions&gt;</c>.
/// </summary>
public sealed class HostingPathTests
{
    private const string Eq501 = """{"equipmentId":"EQ-501"}""";

    [Fact]
    public async Task MapAGUIServer_reads_the_options_from_dependency_injection()
    {
        var model = WarrantyModel();
        await using var app = await StartAgentFrameworkAsync(
            services => services.Configure<AGUIStreamOptions>(options => MapResultRecipe.Apply(options)),
            endpoints => endpoints.MapAGUIServer("/agent", model.AsAIAgent(name: "beacon", tools: [MapResultRecipe.CheckWarranty])));

        var frames = await AGUIWire.RunRawAsync(app.GetTestClient(), "/agent", Turns.Run("Is EQ-501 still under warranty?"), Ct);
        frames.Capture("10-map-agui-server");

        var result = frames.IndexOf("TOOL_CALL_RESULT");
        Assert.Equal(("CUSTOM", MapResultRecipe.WarrantyCheckedEvent), (frames[result + 1].Type(), frames[result + 1].Str("name")));
        Assert.Empty(frames.SelectMany(f => AGUIWire.NullProperties(f)));
    }

    [Fact]
    public async Task Endpoint_metadata_replaces_the_options_from_dependency_injection()
    {
        var model = WarrantyModel();
        await using var app = await StartAgentFrameworkAsync(
            services => services.Configure<AGUIStreamOptions>(options =>
                options.MapResult(MapResultRecipe.CheckWarrantyName, _ => [new CustomEvent { Name = "from-options" }])),
            endpoints => endpoints
                .MapAGUIServer("/agent", model.AsAIAgent(name: "beacon", tools: [MapResultRecipe.CheckWarranty]))
                .WithMetadata(MapResultRecipe.Apply(new AGUIStreamOptions())));

        var frames = await AGUIWire.RunRawAsync(app.GetTestClient(), "/agent", Turns.Run("Is EQ-501 still under warranty?"), Ct);

        // Not merged: the metadata's options are used instead of the ones in DI, not as well as them.
        Assert.Equal([MapResultRecipe.WarrantyCheckedEvent], frames.Where(f => f.Type() == "CUSTOM").Select(f => f.Str("name")));
    }

    [Fact]
    public async Task MapAGUIServer_shares_one_options_instance_across_requests()
    {
        var model = new RecordedChatClient(
            Turns.Call("call-1", MapResultRecipe.CheckWarrantyName, Eq501), Turns.Text("msg-2", "Covered."),
            Turns.Call("call-2", MapResultRecipe.CheckWarrantyName, Eq501), Turns.Text("msg-4", "Covered."));
        await using var app = await StartAgentFrameworkAsync(
            services => services.Configure<AGUIStreamOptions>(options => CountResults(options)),
            endpoints => endpoints.MapAGUIServer("/agent", model.AsAIAgent(name: "beacon", tools: [MapResultRecipe.CheckWarranty])));

        var first = await AGUIWire.RunRawAsync(app.GetTestClient(), "/agent", Turns.Run("Is EQ-501 covered?", runId: "run-1"), Ct);
        var second = await AGUIWire.RunRawAsync(app.GetTestClient(), "/agent", Turns.Run("Is EQ-501 covered?", runId: "run-2"), Ct);

        // The counter lives in the options, and the second run sees the first run's count.
        Assert.Equal((1, 2), (Count(first), Count(second)));
    }

    [Fact]
    public async Task MapAGUIEndpoint_builds_the_options_for_each_request()
    {
        var model = new RecordedChatClient(
            Turns.Call("call-1", MapResultRecipe.CheckWarrantyName, Eq501), Turns.Text("msg-2", "Covered."),
            Turns.Call("call-2", MapResultRecipe.CheckWarrantyName, Eq501), Turns.Text("msg-4", "Covered."));
        await using var host = await CookbookHost.StartAsync(Pipelines.WithTools(model, MapResultRecipe.CheckWarranty), CountResults);

        var first = await host.RunAsync(Turns.Run("Is EQ-501 covered?", runId: "run-1"));
        var second = await host.RunAsync(Turns.Run("Is EQ-501 covered?", runId: "run-2"));

        Assert.Equal((1, 1), (Count(first), Count(second)));
    }

    [Fact]
    public async Task Raw_events_pass_through_an_Agent_Framework_agent_too()
    {
        var pipeline = new ChatClientBuilder(new RecordedChatClient(Turns.Text("msg-1", "CAP-45-5 is in stock in Portsmouth.")))
            .UseProgressActivity("Checking parts for WO-1001")
            .Build();
        await using var app = await StartAgentFrameworkAsync(
            _ => { },
            endpoints => endpoints.MapAGUIServer("/agent", pipeline.AsAIAgent(name: "beacon")));

        var frames = await AGUIWire.RunRawAsync(app.GetTestClient(), "/agent", Turns.Run("Is CAP-45-5 in stock?"), Ct);

        Assert.Equal("activity-run-1", frames.Only("ACTIVITY_SNAPSHOT").Str("messageId"));
        Assert.Equal("activity-run-1", frames.Only("ACTIVITY_DELTA").Str("messageId"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RecordedChatClient WarrantyModel() => new(
        Turns.Call("call-1", MapResultRecipe.CheckWarrantyName, Eq501),
        Turns.Text("msg-2", "EQ-501's parts are covered until May 14, 2029."));

    // A mapper with state: it numbers the warranty results it has seen.
    private static AGUIStreamOptions CountResults(AGUIStreamOptions options)
    {
        var seen = 0;
        return options.MapResult(MapResultRecipe.CheckWarrantyName, _ =>
            [new CustomEvent { Name = "results_seen", Value = JsonSerializer.SerializeToElement(++seen, AGUIJsonSerializerContext.Default.Int32) }]);
    }

    private static int Count(IReadOnlyList<JsonElement> frames) => frames.Only("CUSTOM").GetProperty("value").GetInt32();

    private static async Task<WebApplication> StartAgentFrameworkAsync(
        Action<IServiceCollection> configureServices,
        Action<WebApplication> mapEndpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddAGUIServer();

        // AddAGUIServer appends AG-UI's resolver after ASP.NET Core's, which writes "parentRunId": null and
        // friends; put AG-UI's first (Chapter 5).
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver));

        configureServices(builder.Services);

        var app = builder.Build();
        mapEndpoints(app);
        await app.StartAsync(Ct);
        return app;
    }
}
