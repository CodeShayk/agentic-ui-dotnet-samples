using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using Beacon.AI;
using Ch21.Beacon.Agent.Interop;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch21.Tests;

/// <summary>
/// The bracket <see cref="SubagentAttributionChatClient"/> puts around a delegating tool: a fresh
/// <c>subagentRunId</c> for every invocation, and an error code that says whose failure it was.
/// </summary>
public sealed class AttributionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_call_ID_that_recurs_in_a_later_run_still_gets_a_new_subagentRunId()
    {
        // A restarted scripted model, or any model, can reuse "call-0001-2"; two invocations must never share an ID.
        var client = Attributed(Quote("call-0001-2"));

        var first = Assert.Single((await RunAsync(client)).OfType<SubagentStartedEvent>());
        var second = Assert.Single((await RunAsync(client)).OfType<SubagentStartedEvent>());

        Assert.Equal(("call-0001-2", "call-0001-2"), (first.ParentToolCallId, second.ParentToolCallId));
        Assert.NotEqual(first.SubagentRunId, second.SubagentRunId);
        Assert.All([first.SubagentRunId!, second.SubagentRunId!], id => Assert.Matches("^northwind-supply-[0-9a-f]{8}$", id));
    }

    [Fact]
    public async Task Arguments_Beacon_refused_are_not_reported_as_Northwinds_failure()
    {
        var refused = await RunAsync(Attributed(Result("call-0001-2", new RefusedRequest("Ask about one SKU, such as EXP-TANK-4, and a quantity from 1 to 10."))));
        var failed = await RunAsync(Attributed(Result("call-0001-2", new ToolError("Northwind Supply's agent didn't answer, so the supplier's stock is unknown."))));

        Assert.Equal("invalid_request", Assert.Single(refused.OfType<SubagentErrorEvent>()).Code);
        Assert.Equal("delegate_failed", Assert.Single(failed.OfType<SubagentErrorEvent>()).Code);
    }

    [Fact]
    public async Task The_supplier_tool_refuses_bad_arguments_without_contacting_Northwind()
    {
        await using var systems = InteropSystems.Start();
        systems.SupplierLink.Down = true; // a call to Northwind would fail and be logged
        var tool = systems.Beacon.Services.GetRequiredService<SupplierAgentTool>().AsAIFunction();

        var result = Assert.IsType<JsonElement>(await tool.InvokeAsync(new AIFunctionArguments { ["sku"] = "CMP-RT120", ["quantity"] = 50 }, Ct));

        Assert.Equal("invalid_request", result.GetProperty("code").GetString());
        Assert.StartsWith("Ask about one SKU", result.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(systems.Beacon.Logs, l => l.Exception is HttpRequestException);
    }

    private static SubagentAttributionChatClient Attributed(params ChatResponseUpdate[] round) =>
        new(new RoundClient(round), new Dictionary<string, SubagentInfo> { [SupplierAgentTool.Name] = InteropExtensions.Supplier });

    private static ChatResponseUpdate[] Quote(string callId) =>
        Result(callId, new SupplierAvailability(SupplierAgentTool.Source, "CMP-RT120", 1, true, 2, true, 1, "Albany, NY"));

    // One round of function invocation as the attribution layer sees it: the call, then its result.
    private static ChatResponseUpdate[] Result<T>(string callId, T result) =>
    [
        new(ChatRole.Assistant, [new FunctionCallContent(callId, SupplierAgentTool.Name, new Dictionary<string, object?> { ["sku"] = "CMP-RT120" })]) { MessageId = "msg-0001" },
        new(ChatRole.Tool, [new FunctionResultContent(callId, JsonSerializer.SerializeToElement(result, InteropJson.Options))]),
    ];

    private static async Task<List<BaseEvent>> RunAsync(IChatClient client)
    {
        List<BaseEvent> events = [];
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Ask Northwind.")], cancellationToken: Ct))
        {
            if (update.RawRepresentation is BaseEvent evt)
            {
                events.Add(evt);
            }
        }

        return events;
    }

    /// <summary>Answers every request with the same recorded round.</summary>
    private sealed class RoundClient(ChatResponseUpdate[] round) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in round)
            {
                await Task.Yield();
                yield return update.Clone();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
