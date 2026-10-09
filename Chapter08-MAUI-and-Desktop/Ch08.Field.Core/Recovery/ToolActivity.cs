using Beacon.Client.Core;

namespace Ch08.Field.Core.Recovery;

/// <summary>
/// One short sentence about what a tool call is doing, for the line under a message. Chapter 9 replaces
/// this with real tool rendering.
/// </summary>
public static class ToolActivity
{
    /// <summary>Describes the calls an assistant message carries, in order.</summary>
    /// <param name="calls">The message's tool calls.</param>
    public static string Describe(IEnumerable<ToolCallView> calls) => string.Join(" ", calls.Select(Describe));

    /// <summary>Describes one call in its current status.</summary>
    /// <param name="call">The call.</param>
    public static string Describe(ToolCallView call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var (doing, done) = Phrases(call);

        return call.Status switch
        {
            ToolCallStatus.Streaming or ToolCallStatus.InProgress => $"{doing}…",
            ToolCallStatus.Completed => $"{done}.",
            ToolCallStatus.AwaitingApproval => $"{doing}: waiting for approval.",
            ToolCallStatus.AwaitingClient => $"{doing}: waiting for this app.",
            _ => $"{doing}: cut off before Beacon heard back.",
        };
    }

    /// <summary>What the call does, in the present and the past tense.</summary>
    /// <param name="call">The call.</param>
    public static (string Doing, string Done) Phrases(ToolCallView call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var workOrder = ToolSafety.Argument(call, "workOrderId") ?? "a work order";
        var equipment = ToolSafety.Argument(call, "equipmentId") ?? "the unit";

        return call.Name switch
        {
            "get_work_order" => ($"Looking up {workOrder}", $"Looked up {workOrder}"),
            "list_work_orders" => ("Listing work orders", "Listed work orders"),
            "search_parts" => ($"Searching parts for {ToolSafety.Argument(call, "query") ?? "a match"}", $"Searched parts for {ToolSafety.Argument(call, "query") ?? "a match"}"),
            "check_warranty" => ($"Checking the warranty on {equipment}", $"Checked the warranty on {equipment}"),
            "get_fault_history" => ($"Reading the fault history of {equipment}", $"Read the fault history of {equipment}"),
            "get_schedule" => ("Reading today's schedule", "Read today's schedule"),
            "place_part_order" => ($"Ordering {ToolSafety.Argument(call, "sku") ?? "a part"} for {workOrder}", $"Ordered {ToolSafety.Argument(call, "sku") ?? "a part"} for {workOrder}"),
            "charge_customer" => ($"Charging the customer for {workOrder}", $"Charged the customer for {workOrder}"),
            _ => ($"Running {call.Name}", $"Ran {call.Name}"),
        };
    }
}
