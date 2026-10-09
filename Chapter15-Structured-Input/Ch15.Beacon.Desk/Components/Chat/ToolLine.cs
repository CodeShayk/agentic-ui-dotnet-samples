using System.Text.Json;
using Beacon.Client.Core;

namespace Ch15.Beacon.Desk.Components.Chat;

/// <summary>One sentence about what a tool call is doing, for the activity line under a message (Chapter 9).</summary>
public static class ToolLine
{
    public static string Describe(IEnumerable<ToolCallView> calls) =>
        string.Join(" ", calls.Select(Describe));

    public static string Describe(ToolCallView call)
    {
        var (doing, done) = call.Name switch
        {
            "get_work_order" => ($"Looking up {Argument(call, "workOrderId") ?? "a work order"}", $"Looked up {Argument(call, "workOrderId") ?? "a work order"}"),
            "list_work_orders" => ("Listing work orders", "Listed work orders"),
            "search_parts" => ($"Searching parts for {Argument(call, "query") ?? "a match"}", $"Searched parts for {Argument(call, "query") ?? "a match"}"),
            "check_warranty" => ($"Checking the warranty on {Argument(call, "equipmentId") ?? "the unit"}", $"Checked the warranty on {Argument(call, "equipmentId") ?? "the unit"}"),
            "get_fault_history" => ($"Reading the fault history of {Argument(call, "equipmentId") ?? "the unit"}", $"Read the fault history of {Argument(call, "equipmentId") ?? "the unit"}"),
            "get_schedule" => ("Reading today's schedule", "Read today's schedule"),
            _ => ($"Running {call.Name}", $"Ran {call.Name}"),
        };

        return call.Status switch
        {
            ToolCallStatus.Streaming or ToolCallStatus.InProgress => $"{doing}…",
            ToolCallStatus.Completed when call.Error is not null => $"{doing}: it failed.",
            ToolCallStatus.Completed => $"{done}.",
            _ => $"{doing}: stopped before it finished.",
        };
    }

    /// <summary>Who runs the call, for the line's small label.</summary>
    public static string Where(ToolCallView call) => call.Executor == ToolExecutor.Agent ? "Beacon" : "";

    // Arguments are untrusted model output; they're only ever shown as text.
    private static string? Argument(ToolCallView call, string name) =>
        call.Arguments is { ValueKind: JsonValueKind.Object } arguments
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
