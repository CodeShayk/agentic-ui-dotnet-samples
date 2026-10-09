using System.Text.Json;
using Beacon.Client.Core;
using Ch10.Beacon.Desk.Tools;

namespace Ch10.Beacon.Desk.Components.Chat;

/// <summary>
/// One sentence about what a tool call is doing, for the activity line under a message. A call the
/// Desk runs itself (a frontend tool) says so, because "Beacon opened WO-1001" means something on this
/// screen changed.
/// </summary>
public static class ToolLine
{
    public static string Describe(IEnumerable<ToolCallView> calls) =>
        string.Join(" ", calls.Select(Describe));

    public static string Describe(ToolCallView call)
    {
        var (doing, done) = call.Name switch
        {
            DeskTools.OpenWorkOrderName => ($"Opening {Argument(call, "workOrderId") ?? "a work order"} on your screen", $"Opened {Argument(call, "workOrderId") ?? "a work order"} on your screen"),
            DeskTools.HighlightScheduleSlotName => ("Highlighting a slot on the schedule", "Highlighted a slot on the schedule"),
            DeskTools.PrefillPartOrderName => ("Asking you before filling in the part order form",
                ResultStatus(call) == "prefilled" ? "Filled in the part order form" : "Left the part order form as it was"),
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
            ToolCallStatus.AwaitingApproval => $"{doing}: waiting for approval.",
            ToolCallStatus.AwaitingClient when call.Executor == ToolExecutor.Client => $"{doing}…",
            ToolCallStatus.AwaitingClient => $"{doing}: no app on this screen can do that.",
            _ => $"{doing}: stopped before it finished.",
        };
    }

    /// <summary>Who runs the call, for the line's small label.</summary>
    public static string Where(ToolCallView call) => call.Executor switch
    {
        ToolExecutor.Client => "this Desk",
        ToolExecutor.Agent => "Beacon",
        _ => "",
    };

    private static string? ResultStatus(ToolCallView call)
    {
        try
        {
            using var result = JsonDocument.Parse(call.Result ?? "");
            return result.RootElement.ValueKind == JsonValueKind.Object && result.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Arguments are untrusted model output; they're only ever shown as text.
    private static string? Argument(ToolCallView call, string name) =>
        call.Arguments is { ValueKind: JsonValueKind.Object } arguments
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
