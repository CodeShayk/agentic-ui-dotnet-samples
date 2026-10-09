using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ch26.Tests.Integration.Infrastructure;

/// <summary>One AG-UI run as it crossed the wire: the request the client sent and the frames that came back.</summary>
public sealed record WireRun(string Input, IReadOnlyList<JsonElement> Frames)
{
    public JsonElement InputJson => JsonDocument.Parse(Input).RootElement.Clone();
}

/// <summary>
/// Beacon's conversations as test data: each one names the recordings that stand in for the model and
/// the requests a client sends. The wire, snapshot and conformance tests all run the same table, so a
/// new scenario is checked by every layer at once.
/// </summary>
public static class Scenarios
{
    public const string Status = "wo-1001-status";
    public const string Order = "order-compressor";
    public const string OrderApproved = "order-compressor-approved";
    public const string LlamaWarranty = "llama-warranty-eq501";
    public const string LlamaStatusWrongTool = "llama-wo-1001-status-wrong-tool";
    public const string LlamaOrderStringQuantity = "llama-order-string-quantity";

    public static TheoryData<string> All { get; } =
        [Status, Order, OrderApproved, LlamaWarranty, LlamaStatusWrongTool, LlamaOrderStringQuantity];

    /// <summary>Runs the scenario against a fresh in-memory agent and returns every run, in order.</summary>
    public static async Task<IReadOnlyList<WireRun>> RunAsync(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        switch (name)
        {
            case Status:
                return [await OneRunAsync([Recordings.Status], Wire.UserRun("thread-status", "wo-1001-status", "What is the status of WO-1001?"))];

            case Order:
                return [await OneRunAsync([Recordings.Order], OrderInput)];

            case OrderApproved:
            {
                await using var host = await AgentHost.ReplayingAsync(Recordings.Order, Recordings.OrderApproved);
                var http = host.CreateClient();
                var asked = new WireRun(OrderInput, await Wire.PostAsync(http, OrderInput, ct));
                var resume = ApproveInput(asked, "order-compressor-approved");
                return [asked, new WireRun(resume, await Wire.PostAsync(http, resume, ct))];
            }

            case LlamaWarranty:
                return [await OneRunAsync([Recordings.LlamaWarranty1], Wire.UserRun("thread-llama", "llama-warranty-eq501", "Is EQ-501 still under warranty?"))];

            case LlamaStatusWrongTool:
                return [await OneRunAsync([Recordings.LlamaStatusWrongTool], Wire.UserRun("thread-llama", "llama-wo-1001-status", "What is the status of WO-1001?"))];

            case LlamaOrderStringQuantity:
                return [await OneRunAsync([Recordings.LlamaOrderStringQuantity], Wire.UserRun("thread-llama", "llama-order", "Order a replacement compressor CMP-CA36 for WO-1001."))];

            default:
                throw new ArgumentException($"No scenario named '{name}'.", nameof(name));
        }
    }

    private static string OrderInput => Wire.UserRun("thread-order", "order-compressor", "Order a replacement compressor CMP-CA36 for WO-1001.");

    private static async Task<WireRun> OneRunAsync(string[] recordings, string input)
    {
        await using var host = await AgentHost.ReplayingAsync(recordings);
        return new WireRun(input, await Wire.PostAsync(host.CreateClient(), input, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The resume a client sends when the dispatcher approves: the history, then a resume entry that answers
    /// the interrupt with the call it concerns, in the payload shape <c>AGUIChatClient</c> writes.
    /// </summary>
    public static string ApproveInput(WireRun asked, string runId, bool approved = true)
    {
        var input = JsonNode.Parse(asked.Input)!.AsObject();
        var frames = asked.Frames;
        var interrupt = frames.OfType("RUN_FINISHED").Single().GetProperty("outcome").GetProperty("interrupts")[0];
        var callId = interrupt.GetProperty("toolCallId").GetString()!;
        var start = frames.OfType("TOOL_CALL_START").Single(f => f.GetProperty("toolCallId").GetString() == callId);
        var arguments = string.Concat(frames.OfType("TOOL_CALL_ARGS")
            .Where(f => f.GetProperty("toolCallId").GetString() == callId)
            .Select(f => f.GetProperty("delta").GetString()));

        var messages = input["messages"]!.AsArray();
        messages.Add(new JsonObject
        {
            ["id"] = start.GetProperty("parentMessageId").GetString(),
            ["role"] = "assistant",
            ["content"] = Wire.Text(frames),
            ["toolCalls"] = new JsonArray(new JsonObject
            {
                ["id"] = callId,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = start.GetProperty("toolCallName").GetString(), ["arguments"] = arguments },
            }),
        });

        input["runId"] = runId;
        input["resume"] = new JsonArray(new JsonObject
        {
            ["interruptId"] = interrupt.GetProperty("id").GetString(),
            ["status"] = "resolved",
            ["payload"] = new JsonObject
            {
                ["approved"] = approved,
                ["toolCall"] = new JsonObject
                {
                    ["callId"] = callId,
                    ["name"] = start.GetProperty("toolCallName").GetString(),
                    ["arguments"] = JsonNode.Parse(arguments),
                },
            },
        });

        return input.ToJsonString();
    }
}
