using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.Testing;

namespace Ch18.Tests.Infrastructure;

/// <summary>Builds runs, sends them and reads the raw frames back, as a client would see them on the wire.</summary>
public static class Runs
{
    private static readonly JsonSerializerOptions WireOptions = new() { TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver };

    public const string Capacitor = "Line up everything for WO-1001's capacitor swap: parts, a slot and the warranty.";
    public const string Compressor = "Priya says the compressor on WO-1001 is seized. Order a replacement compressor, find her a slot and check the warranty.";
    public const string Rooftop = "Line up everything for WO-1005's compressor: parts, a slot and the warranty.";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static RunAgentInput Turn(string threadId, string runId, string text) => new()
    {
        ThreadId = threadId,
        RunId = runId,
        Messages = [new AGUIUserMessage { Id = "user-1", Content = text }],
    };

    /// <summary>A resuming run: the same history, plus the answers.</summary>
    public static RunAgentInput Resume(string threadId, string runId, string text, params AGUIResume[] answers)
    {
        var input = Turn(threadId, runId, text);
        input.Resume = [.. answers];
        return input;
    }

    /// <summary>An approval shaped the way AGUIChatClient writes one: <c>approved</c> plus the call it saw.</summary>
    public static AGUIResume SdkApproval(string interruptId, bool approved, string callId) => new()
    {
        InterruptId = interruptId,
        Status = ResumeStatus.Resolved,
        Payload = JsonDocument.Parse(new JsonObject
        {
            ["approved"] = approved,
            ["toolCall"] = new JsonObject
            {
                ["callId"] = callId,
                ["name"] = "place_part_order",
                ["arguments"] = new JsonObject { ["workOrderId"] = "WO-1001", ["sku"] = "CMP-CA36", ["quantity"] = 1 },
            },
        }.ToJsonString()).RootElement.Clone(),
    };

    /// <summary>Runs and returns every <c>data:</c> payload exactly as the server wrote it.</summary>
    public static Task<IReadOnlyList<JsonElement>> RawAsync(HttpClient http, RunAgentInput input) =>
        AGUIWire.RunRawAsync(http, "/agent", input, Ct);

    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, RunAgentInput input)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = JsonContent.Create(input, WireOptions.GetTypeInfo(typeof(RunAgentInput))),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
    }

    public static string Type(this JsonElement frame) => frame.GetProperty("type").GetString()!;

    public static string? Str(this JsonElement frame, string property) =>
        frame.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static string? Owner(this JsonElement frame) => frame.Str("subagentRunId");

    /// <summary>The <c>subagentRunId</c> of the invocation with this name.</summary>
    public static string InvocationOf(IReadOnlyList<JsonElement> frames, string name) =>
        frames.Single(f => f.Type() == "SUBAGENT_STARTED" && f.Str("name") == name).Owner()!;

    /// <summary>Everything one owner said, in order (<see langword="null"/> is the coordinator).</summary>
    public static string TextOf(IReadOnlyList<JsonElement> frames, string? owner) =>
        string.Concat(frames.Where(f => f.Type() == "TEXT_MESSAGE_CONTENT" && f.Owner() == owner).Select(f => f.Str("delta")));

    public static JsonElement Interrupt(IReadOnlyList<JsonElement> frames)
    {
        var outcome = frames[^1].GetProperty("outcome");
        Assert.Equal("interrupt", outcome.Str("type"));
        return Assert.Single(outcome.GetProperty("interrupts").EnumerateArray());
    }
}
