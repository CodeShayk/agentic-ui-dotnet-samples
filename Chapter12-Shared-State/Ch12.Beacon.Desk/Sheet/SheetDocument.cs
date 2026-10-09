using System.Text.Json.Nodes;

namespace Ch12.Beacon.Desk.Sheet;

/// <summary>A checklist line as the Desk shows it.</summary>
public sealed record SheetItem(string Id, string Text, bool Done);

/// <summary>The work order on the sheet, read from the conversation's shared state for display.</summary>
public sealed record SheetView(
    string Id,
    string Customer,
    string Equipment,
    string Summary,
    string Priority,
    string Technician,
    string Status,
    IReadOnlyList<SheetItem> Checklist,
    IReadOnlyList<string> Notes);

/// <summary>
/// Reads the shared sheet out of the conversation's state. The Desk is a tolerant reader: it uses the
/// members it knows and ignores the rest, so a newer agent can add fields without breaking it. The
/// agent is the strict one, because what it reads comes from clients it doesn't control.
/// </summary>
public static class SheetDocument
{
    public const string WorkOrderPath = "/workOrder";
    public const string ChecklistPath = "/workOrder/checklist";
    public const string NotesPath = "/workOrder/notes";

    public static JsonObject? WorkOrder(JsonNode? document) => (document as JsonObject)?["workOrder"] as JsonObject;

    public static JsonArray? Checklist(JsonNode? document) => WorkOrder(document)?["checklist"] as JsonArray;

    public static JsonArray? Notes(JsonNode? document) => WorkOrder(document)?["notes"] as JsonArray;

    /// <summary>The index of the item with this ID, or -1. Indexes are positions now, not identities.</summary>
    public static int IndexOf(JsonNode? document, string itemId)
    {
        var checklist = Checklist(document);
        for (var i = 0; i < (checklist?.Count ?? 0); i++)
        {
            if (Text(checklist![i], "id") == itemId)
            {
                return i;
            }
        }

        return -1;
    }

    public static JsonObject? Item(JsonNode? document, string itemId) =>
        IndexOf(document, itemId) is >= 0 and var index ? Checklist(document)![index] as JsonObject : null;

    public static string? Text(JsonNode? node, string name) =>
        (node as JsonObject)?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool IsDone(JsonNode? item) =>
        (item as JsonObject)?["done"] is JsonValue value && value.TryGetValue<bool>(out var done) && done;

    /// <summary>The sheet for display, or <see langword="null"/> when no work order is open.</summary>
    public static SheetView? Read(JsonNode? document)
    {
        if (WorkOrder(document) is not { } workOrder || Text(workOrder, "id") is not { } id)
        {
            return null;
        }

        return new SheetView(
            id,
            Text(workOrder, "customer") ?? "",
            Text(workOrder, "equipment") ?? "",
            Text(workOrder, "summary") ?? "",
            Text(workOrder, "priority") ?? "",
            Text(workOrder, "technician") ?? "",
            Text(workOrder, "status") ?? "",
            [.. (Checklist(document) ?? []).OfType<JsonObject>()
                .Select(item => new SheetItem(Text(item, "id") ?? "", Text(item, "text") ?? "", IsDone(item)))],
            [.. (Notes(document) ?? []).OfType<JsonValue>()
                .Select(note => note.TryGetValue<string>(out var text) ? text : null).OfType<string>()]);
    }
}
