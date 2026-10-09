using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.Testing;

namespace Ch23.Tests.Infrastructure;

/// <summary>Builds runs and reads what came back, as a client would.</summary>
public static class Runs
{
    private static readonly JsonSerializerOptions WireOptions = new() { TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver };

    public static AGUIUserMessage User(string id, string text) => new() { Id = id, Content = text };

    public static RunAgentInput Turn(string threadId, string runId, params AGUIMessage[] messages) => new()
    {
        ThreadId = threadId,
        RunId = runId,
        Messages = [.. messages],
    };

    /// <summary>A resume entry answering an approval, optionally echoing the call it approves.</summary>
    public static AGUIResume Answer(string interruptId, bool approved, JsonObject? toolCall = null, string status = "resolved")
    {
        var payload = new JsonObject { ["approved"] = approved };
        if (toolCall is not null)
        {
            payload["toolCall"] = toolCall;
        }

        return new AGUIResume { InterruptId = interruptId, Status = status, Payload = ToElement(payload) };
    }

    public static JsonObject Call(string callId, string name, JsonObject arguments) =>
        new() { ["callId"] = callId, ["name"] = name, ["arguments"] = arguments };

    /// <summary>Runs and returns the events; fails the test unless the server answered 200.</summary>
    public static Task<IReadOnlyList<BaseEvent>> RunAsync(HttpClient http, RunAgentInput input, CancellationToken ct) =>
        AGUIWire.RunAsync(http, "/agent", input, ct);

    /// <summary>Posts a run and returns the raw response, for status codes and problem details.</summary>
    public static Task<HttpResponseMessage> PostAsync(HttpClient http, RunAgentInput input, CancellationToken ct) =>
        SendAsync(http, JsonContent.Create(input, WireOptions.GetTypeInfo(typeof(RunAgentInput))), ct);

    /// <summary>Posts a body exactly as written.</summary>
    public static Task<HttpResponseMessage> PostJsonAsync(HttpClient http, string json, CancellationToken ct) =>
        SendAsync(http, new StringContent(json, Encoding.UTF8, "application/json"), ct);

    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    public static RunFinishedEvent Finished(IReadOnlyList<BaseEvent> events) => Assert.IsType<RunFinishedEvent>(events[^1]);

    public static AGUIInterrupt SingleInterrupt(IReadOnlyList<BaseEvent> events) =>
        Assert.Single(Assert.IsType<RunFinishedInterruptOutcome>(Finished(events).Outcome).Interrupts);

    public static string Text(IEnumerable<BaseEvent> events) =>
        string.Concat(events.OfType<TextMessageContentEvent>().Select(e => e.Delta));

    public static JsonElement ToElement(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpContent content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent") { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}
