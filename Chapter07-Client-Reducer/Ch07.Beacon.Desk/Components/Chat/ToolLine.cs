using System.Text.Json;
using Beacon.Client.Core;

namespace Ch07.Beacon.Desk.Components.Chat;

/// <summary>
/// One sentence about what a tool call is doing, for the activity line under a message. Chapter 9
/// replaces this with real tool rendering; for now a dispatcher just needs to know Beacon is busy and why.
/// </summary>
public static class ToolLine
{
    public static string Describe(IEnumerable<ToolCallView> calls) =>
        string.Join(" ", calls.Select(Describe));

    /// <summary>
    /// What a screen reader hears while a tool runs: the latest call whose arguments are complete and
    /// whose result hasn't arrived, or nothing. One phrase per call, cleared when the call finishes.
    /// </summary>
    public static string Announcement(ConversationView view) =>
        view.ToolCalls.LastOrDefault(c => c.Status == ToolCallStatus.InProgress) is { } running
        && Phrases(running).Doing is var doing
            ? $"Beacon is {char.ToLowerInvariant(doing[0])}{doing[1..]}…"
            : string.Empty;

    public static string Describe(ToolCallView call)
    {
        var (doing, done) = Phrases(call);

        return call.Status switch
        {
            ToolCallStatus.Streaming or ToolCallStatus.InProgress => $"{doing}…",
            ToolCallStatus.Completed => $"{done}.",
            ToolCallStatus.AwaitingApproval => $"{doing}: waiting for approval.",
            ToolCallStatus.AwaitingClient => $"{doing}: waiting for this app.",
            _ => $"{doing}: stopped before it finished.",
        };
    }

    private static (string Doing, string Done) Phrases(ToolCallView call) => call.Name switch
    {
        "get_work_order" => ($"Looking up {Argument(call, "workOrderId") ?? "a work order"}", $"Looked up {Argument(call, "workOrderId") ?? "a work order"}"),
        "list_work_orders" => ("Listing work orders", "Listed work orders"),
        "search_parts" => ($"Searching parts for {Argument(call, "query") ?? "a match"}", $"Searched parts for {Argument(call, "query") ?? "a match"}"),
        "check_warranty" => ($"Checking the warranty on {Argument(call, "equipmentId") ?? "the unit"}", $"Checked the warranty on {Argument(call, "equipmentId") ?? "the unit"}"),
        "get_fault_history" => ($"Reading the fault history of {Argument(call, "equipmentId") ?? "the unit"}", $"Read the fault history of {Argument(call, "equipmentId") ?? "the unit"}"),
        "get_schedule" => ("Reading today's schedule", "Read today's schedule"),
        _ => ($"Running {call.Name}", $"Ran {call.Name}"),
    };

    // Arguments are untrusted model output; they're only ever shown as text.
    private static string? Argument(ToolCallView call, string name) =>
        call.Arguments is { ValueKind: JsonValueKind.Object } arguments
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
