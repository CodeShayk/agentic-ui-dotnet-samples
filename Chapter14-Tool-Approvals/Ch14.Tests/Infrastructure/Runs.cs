using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Testing;

namespace Ch14.Tests.Infrastructure;

/// <summary>Builds runs, sends them and reads what came back, as a client would.</summary>
public static class Runs
{
    private static readonly JsonSerializerOptions WireOptions = new() { TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver };

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static JsonObject CompressorOrder => new() { ["workOrderId"] = "WO-1001", ["sku"] = "CMP-CA36", ["quantity"] = 1 };

    public static AGUIUserMessage User(string id, string text) => new() { Id = id, Content = text };

    public static RunAgentInput Turn(string threadId, string runId, params AGUIMessage[] messages) => new()
    {
        ThreadId = threadId,
        RunId = runId,
        Messages = [.. messages],
    };

    /// <summary>A resuming run with no new messages, carrying only the answers.</summary>
    public static RunAgentInput Resume(string threadId, string runId, params AGUIResume[] answers)
    {
        var input = Turn(threadId, runId);
        input.Resume = [.. answers];
        return input;
    }

    /// <summary>A resume entry with any payload.</summary>
    public static AGUIResume Answer(string interruptId, JsonObject? payload, string status = "resolved") => new()
    {
        InterruptId = interruptId,
        Status = status,
        Payload = payload is null ? null : ToElement(payload),
    };

    /// <summary>An approval shaped the way AGUIChatClient writes one: <c>approved</c> plus the call it saw.</summary>
    public static AGUIResume SdkApproval(string interruptId, bool approved, string callId, string toolName, JsonObject arguments) =>
        Answer(interruptId, new JsonObject
        {
            ["approved"] = approved,
            ["toolCall"] = new JsonObject { ["callId"] = callId, ["name"] = toolName, ["arguments"] = arguments },
        });

    /// <summary>Runs and returns the events; fails the test unless the server answered 200.</summary>
    public static Task<IReadOnlyList<BaseEvent>> RunAsync(HttpClient http, RunAgentInput input) =>
        AGUIWire.RunAsync(http, "/agent", input, Ct);

    /// <summary>Posts a run and returns the raw response, for status codes and problem details.</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, RunAgentInput input)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = JsonContent.Create(input, WireOptions.GetTypeInfo(typeof(RunAgentInput))),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
    }

    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    public static RunFinishedEvent Finished(IReadOnlyList<BaseEvent> events) => Assert.IsType<RunFinishedEvent>(events[^1]);

    public static AGUIInterrupt SingleInterrupt(IReadOnlyList<BaseEvent> events) =>
        Assert.Single(Assert.IsType<RunFinishedInterruptOutcome>(Finished(events).Outcome).Interrupts);

    public static string Text(IEnumerable<BaseEvent> events) =>
        string.Concat(events.OfType<TextMessageContentEvent>().Select(e => e.Delta));

    public static JsonElement ToElement(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    /// <summary>Dana asks for the WO-1001 compressor; the run ends asking her.</summary>
    public static async Task<(AGUIInterrupt Interrupt, string CallId)> AskForCompressorAsync(HttpClient http, string threadId = "thread-1")
    {
        var events = await RunAsync(http, Turn(threadId, "run-1",
            User("user-1", "Priya says the capacitor tests fine. Order a replacement compressor for WO-1001.")));
        var interrupt = SingleInterrupt(events);
        Assert.Equal(BeaconTools.PlacePartOrderName, events.OfType<ToolCallStartEvent>().Single(s => s.ToolCallId == interrupt.ToolCallId).ToolCallName);
        return (interrupt, interrupt.ToolCallId!);
    }

    /// <summary>The AG-UI declaration of the Desk's frontend tool from Chapter 10.</summary>
    public static AGUITool ShowWorkOrderTool { get; } = new()
    {
        Name = TestModel.ShowWorkOrderTool,
        Description = "Opens a work order on the dispatcher's screen.",
        Parameters = ToElement(JsonNode.Parse("""{"type":"object","properties":{"workOrderId":{"type":"string"}},"required":["workOrderId"]}""")!),
    };
}
