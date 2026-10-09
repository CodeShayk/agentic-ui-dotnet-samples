using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Formatting;
using Beacon.Client.Core;
using Ch12.Beacon.Desk.Sheet;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ch12.Tests;

/// <summary>Ch12.Beacon.Agent in process, with the scripted model streaming and the tools answering at once.</summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Ch12.Beacon.Agent.Program> _factory =
        new WebApplicationFactory<Ch12.Beacon.Agent.Program>().WithWebHostBuilder(host => host
            .UseSetting("AI:ScriptedChunkDelayMilliseconds", "0")
            .UseSetting("Beacon:ToolDelayMilliseconds", "0"));

    /// <summary>A client whose requests are recorded, so a test can read the state a run carried.</summary>
    public HttpClient CreateClient(RequestRecorder? recorder = null) =>
        recorder is null ? _factory.CreateClient() : _factory.CreateDefaultClient(recorder);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

/// <summary>Keeps the body of every request, as the agent received it.</summary>
public sealed class RequestRecorder : DelegatingHandler
{
    public List<JsonElement> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Bodies)
            {
                Bodies.Add(JsonDocument.Parse(body).RootElement.Clone());
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    /// <summary>The <c>state</c> the last run carried.</summary>
    public JsonNode? LastState => Bodies[^1].TryGetProperty("state", out var state) ? JsonNode.Parse(state.GetRawText()) : null;
}

/// <summary>Beacon Desk's client stack without the browser: tap, AGUIChatClient, conversation and the sheet editor.</summary>
public sealed class DeskStack : IDisposable
{
    public DeskStack(HttpClient http)
    {
        Tap = WireEventTap.ForHttp(http, "/agent");
        Client = new AGUIChatClient(new AGUIChatClientOptions { Transport = Tap });
        Conversation = new AgentConversation(Client, new ConversationOptions
        {
            Tap = Tap,
            ConfigureInput = (input, _) => input.Context = Board.ToContext(),
        });
        Editor = new SheetEditor(Conversation); // subscribes first, as in the Desk, where the panel creates it

        Conversation.Changed += (_, e) =>
        {
            lock (Views)
            {
                Views.Add(e.View);
            }
        };
    }

    public DispatcherBoard Board { get; } = new();

    public WireEventTap Tap { get; }

    public AGUIChatClient Client { get; }

    public AgentConversation Conversation { get; }

    public SheetEditor Editor { get; }

    /// <summary>Every view the conversation published, in order.</summary>
    public List<ConversationView> Views { get; } = [];

    public JsonNode? Document => Conversation.View.State.Document;

    public Task SendAsync(string text) =>
        Conversation.SendAsync(text, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Runs <paramref name="edit"/> once, the first time <paramref name="when"/> matches a change, on the thread that made it.</summary>
    public void EditWhen(Func<ConversationChangedEventArgs, bool> when, Action edit)
    {
        var done = 0;
        Conversation.Changed += (_, e) =>
        {
            if (when(e) && Interlocked.Exchange(ref done, 1) == 0)
            {
                edit();
            }
        };
    }

    public void Dispose()
    {
        Editor.Dispose();
        Conversation.Dispose();
        Client.Dispose();
    }
}

/// <summary>Posts runs and reads the raw SSE frames back, never re-serialized.</summary>
public static class Runs
{
    public static RunAgentInput Input(string text, JsonNode? state = null, string threadId = "t-1", string runId = "r-1", IList<AGUIContext>? context = null) => new()
    {
        ThreadId = threadId,
        RunId = runId,
        Messages = [new AGUIUserMessage { Id = $"{runId}-user", Content = text }],
        State = state is null ? null : JsonDocument.Parse(state.ToJsonString()).RootElement.Clone(),
        Context = context ?? [],
    };

    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, RunAgentInput input) =>
        await http.PostAsJsonAsync("/agent", input, AGUIJsonSerializerContext.Default.RunAgentInput, TestContext.Current.CancellationToken);

    public static string Type(JsonElement frame) => frame.GetProperty("type").GetString()!;

    public static JsonNode Node(JsonElement element) => JsonNode.Parse(element.GetRawText())!;

    /// <summary>The sheet the agent opens for WO-1001, taken from a real run.</summary>
    public static async Task<JsonNode> OpenSheetAsync(HttpClient http)
    {
        var frames = await global::Beacon.Testing.AGUIWire.RunRawAsync(http, "/agent", Input("Open WO-1001"), TestContext.Current.CancellationToken);
        return Node(frames.Last(f => Type(f) == "STATE_SNAPSHOT").GetProperty("snapshot"));
    }

    /// <summary>Applies a STATE_DELTA's patch with the same applier the Desk uses.</summary>
    public static JsonNode? Apply(JsonNode? document, JsonElement patch)
    {
        var result = JsonPatch.Apply(document, patch);
        Assert.True(result.Succeeded, result.Error);
        return result.Document;
    }

    /// <summary>JSON written for humans, parsed for tests.</summary>
    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static string SseType => SseEventStreamFormatter.ServerSentEventsMediaType;
}
