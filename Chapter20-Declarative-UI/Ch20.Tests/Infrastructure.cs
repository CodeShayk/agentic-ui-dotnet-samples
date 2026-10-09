using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using AGUI.Formatting;
using Beacon.Testing;
using Ch20.Beacon.Desk.Components.Surfaces;
using Ch20.DeclarativeUi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ch20.Tests;

/// <summary>
/// Streams captured with <c>curl -N</c> from Ch20.Beacon.Agent on port 5200, and the request and response
/// of one form submission captured from the real client stack against it.
/// </summary>
public static class Recordings
{
    public const string Wo1003 = "inspection-wo-1003.sse";
    public const string Wo1001 = "inspection-wo-1001.sse";
    public const string Wo1005 = "inspection-wo-1005.sse";
    public const string Submit = "submit-wo-1003.sse";
    public const string SubmitRequest = "submit-request.json";

    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Recordings", name);

    /// <summary>The events, read back with the SDK's own SSE reader.</summary>
    public static async Task<IReadOnlyList<BaseEvent>> LoadAsync(string name)
    {
        await using var file = File.OpenRead(PathOf(name));
        List<BaseEvent> events = [];
        await foreach (var evt in new SseEventStreamFormatter().ReadAsync(file, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        return events;
    }

    /// <summary>The <c>data:</c> payloads exactly as written, never re-serialized.</summary>
    public static IReadOnlyList<JsonElement> Frames(string name) =>
        [.. File.ReadLines(PathOf(name))
            .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone())];

    /// <summary>The content of the recording's one <c>a2ui-surface</c> activity.</summary>
    public static JsonObject SurfaceContent(string name) =>
        (JsonObject)JsonNode.Parse(Frames(name).Single(f => f.GetProperty("type").GetString() == "ACTIVITY_SNAPSHOT").GetProperty("content").GetRawText())!;

    /// <summary>The recording's operations array, ready to be changed by a test.</summary>
    public static JsonArray Operations(string name) => (JsonArray)SurfaceContent(name)[A2ui.OperationsKey]!.DeepClone();
}

/// <summary>The validators the two ends run, and helpers to edit a surface's components.</summary>
public static class Surfaces
{
    public static SurfaceValidator Producer { get; } = new(SurfacePolicy.Producer(BeaconCatalog.FieldForms, BeaconUrls.Policy));

    public static SurfaceValidator Desk { get; } = DeskCatalog.CreateValidator();

    public static JsonArray Components(JsonArray operations) => (JsonArray)operations[1]!["updateComponents"]!["components"]!;

    public static JsonObject Component(JsonArray operations, string id) =>
        Components(operations).OfType<JsonObject>().Single(c => c["id"]!.GetValue<string>() == id);

    /// <summary>Adds a component and lists it among the form's children, after <paramref name="after"/>.</summary>
    public static void Insert(JsonArray operations, JsonObject component, string after = "readings-heading")
    {
        Components(operations).Add(component);
        var children = (JsonArray)Component(operations, "form")["children"]!;
        var index = children.Select(c => c!.GetValue<string>()).ToList().IndexOf(after) + 1;
        children.Insert(index, JsonValue.Create(component["id"]!.GetValue<string>()));
    }

    public static SurfaceInstance Instance(JsonArray operations, SurfaceValidator? validator = null) =>
        SurfaceInstance.FromActivity("a2ui-surface-test", SurfaceOperations.ActivityContent(operations), validator ?? Desk);

    public static IEnumerable<RenderNode> All(RenderNode node) => [node, .. node.Children.SelectMany(All)];

    public static RenderNode Node(SurfaceInstance surface, string id) => All(surface.Tree()!).First(n => n.Component.Id == id);
}

/// <summary>Ch20.Beacon.Agent in memory, as its <c>Program.Main</c> builds it, with no delays.</summary>
public sealed class AgentHost : WebApplicationFactory<Ch20.Beacon.Agent.Program>
{
    /// <summary>Test-only service registrations, applied after the agent's own (set before the first request).</summary>
    public Action<IServiceCollection>? TestServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("AI:Provider", "Scripted");
        builder.UseSetting("AI:ScriptedChunkDelayMilliseconds", "0");
        if (TestServices is not null)
        {
            builder.ConfigureTestServices(TestServices);
        }
    }

    public Task<IReadOnlyList<JsonElement>> RunRawAsync(string text, string threadId = "t-20", string runId = "r-1") =>
        AGUIWire.RunRawAsync(CreateClient(), "/agent", AGUIWire.UserRun(text, threadId, runId), TestContext.Current.CancellationToken);
}

public static class Frame
{
    public static string Type(JsonElement frame) => frame.GetProperty("type").GetString()!;
}
