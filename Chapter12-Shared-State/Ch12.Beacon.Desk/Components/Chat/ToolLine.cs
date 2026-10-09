// Adapted from Chapter 7: the same activity line, with sentences for the sheet tools.
using System.Text.Json;
using Beacon.Client.Core;

namespace Ch12.Beacon.Desk.Components.Chat;

/// <summary>One sentence about what a tool call is doing, for the activity line under a message.</summary>
public static class ToolLine
{
    public static string Describe(IEnumerable<ToolCallView> calls) =>
        string.Join(" ", calls.Select(Describe));

    public static string Describe(ToolCallView call)
    {
        var (doing, done) = call.Name switch
        {
            "open_work_order_sheet" => ($"Opening {Argument(call, "workOrderId") ?? "a work order"} on the sheet", $"Opened {Argument(call, "workOrderId") ?? "a work order"} on the sheet"),
            "set_checklist_item" => ($"Updating checklist item {Argument(call, "itemId") ?? ""}", $"Updated checklist item {Argument(call, "itemId") ?? ""}"),
            "add_checklist_item" => ("Adding a checklist item", "Added a checklist item"),
            "add_work_order_note" => ("Adding a note", "Added a note"),
            "get_work_order" => ($"Looking up {Argument(call, "workOrderId") ?? "a work order"}", $"Looked up {Argument(call, "workOrderId") ?? "a work order"}"),
            "get_schedule" => ("Reading today's schedule", "Read today's schedule"),
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
