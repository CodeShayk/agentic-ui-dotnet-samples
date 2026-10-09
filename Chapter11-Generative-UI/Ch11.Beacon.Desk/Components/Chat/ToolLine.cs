// Copied from Chapter 7 (Ch07.Beacon.Desk) with place_part_order added; each chapter's code is a self-contained checkpoint.
using System.Text.Json;
using Beacon.Client.Core;

namespace Ch11.Beacon.Desk.Components.Chat;

/// <summary>
/// One sentence about what a tool call is doing, for the activity line under a message. Component
/// calls never get here: Chapter 11's Desk renders those as components.
/// </summary>
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
            "place_part_order" => ($"Ordering {Argument(call, "sku") ?? "a part"}", $"Ordered {Argument(call, "sku") ?? "a part"}"),
            _ => ($"Running {call.Name}", $"Ran {call.Name}"),
        };

        return call.Status switch
        {
            ToolCallStatus.Streaming or ToolCallStatus.InProgress => $"{doing}…",
            ToolCallStatus.Completed => $"{done}.",
            ToolCallStatus.AwaitingApproval => $"{doing}: waiting for approval.",
            ToolCallStatus.AwaitingClient => $"{doing}: waiting for this app.",
            _ => $"{doing}: stopped before it finished.",
        };
    }

    // Arguments are untrusted model output; they're only ever shown as text.
    private static string? Argument(ToolCallView call, string name) =>
        call.Arguments is { ValueKind: JsonValueKind.Object } arguments
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
