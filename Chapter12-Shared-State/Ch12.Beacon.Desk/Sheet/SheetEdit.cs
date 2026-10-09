using System.Text.Json.Nodes;

namespace Ch12.Beacon.Desk.Sheet;

/// <summary>
/// One change the dispatcher makes to the sheet. An edit names its target by checklist item ID, not
/// by index, and compiles to JSON Patch against whatever document it's applied to, so the editor can
/// apply it again on top of anything the agent sends. Applying an edit that is already in effect does nothing.
/// </summary>
public abstract record SheetEdit
{
    /// <summary>
    /// The JSON Patch that makes this edit to <paramref name="document"/>: empty when the edit is already
    /// in effect, <see langword="null"/> when its target isn't there.
    /// </summary>
    public abstract JsonArray? PatchFor(JsonNode? document);

    /// <summary>
    /// The value this edit sets, as <paramref name="document"/> has it. When two versions of the sheet
    /// disagree about it, someone else changed the same thing.
    /// </summary>
    public virtual JsonNode? Target(JsonNode? document) => null;

    /// <summary>What the edit is about, in words the dispatcher recognizes.</summary>
    public abstract string Subject(JsonNode? document);

    protected static JsonObject Op(string op, string path, JsonNode? value = null)
    {
        var operation = new JsonObject { ["op"] = op, ["path"] = path };
        if (op is "add" or "replace" or "test")
        {
            operation["value"] = value;
        }

        return operation;
    }

    protected static string ItemName(JsonNode? document, string itemId) =>
        SheetDocument.Text(SheetDocument.Item(document, itemId), "text") is { } text ? $"'{text}'" : $"item {itemId}";
}

/// <summary>Ticks or clears a checklist item.</summary>
public sealed record SetItemDone(string ItemId, bool Done) : SheetEdit
{
    public override JsonArray? PatchFor(JsonNode? document)
    {
        var index = SheetDocument.IndexOf(document, ItemId);
        if (index < 0)
        {
            return null;
        }

        if (SheetDocument.IsDone(SheetDocument.Checklist(document)![index]) == Done)
        {
            return new JsonArray();
        }

        var path = $"{SheetDocument.ChecklistPath}/{index}";
        return new JsonArray(Op("test", $"{path}/id", ItemId), Op("replace", $"{path}/done", Done));
    }

    public override JsonNode? Target(JsonNode? document) => SheetDocument.Item(document, ItemId)?["done"]?.DeepClone();

    public override string Subject(JsonNode? document) => ItemName(document, ItemId);
}

/// <summary>Removes a checklist item.</summary>
public sealed record RemoveItem(string ItemId) : SheetEdit
{
    public override JsonArray? PatchFor(JsonNode? document)
    {
        var index = SheetDocument.IndexOf(document, ItemId);
        if (index < 0)
        {
            return SheetDocument.WorkOrder(document) is null ? null : new JsonArray(); // already gone
        }

        var path = $"{SheetDocument.ChecklistPath}/{index}";
        return new JsonArray(Op("test", $"{path}/id", ItemId), Op("remove", path));
    }

    public override JsonNode? Target(JsonNode? document) => SheetDocument.Item(document, ItemId)?.DeepClone();

    public override string Subject(JsonNode? document) => ItemName(document, ItemId);
}

/// <summary>Adds a checklist item with an ID the Desk minted (D1, D2, ...).</summary>
public sealed record AddItem(string ItemId, string Text) : SheetEdit
{
    public override JsonArray? PatchFor(JsonNode? document) =>
        SheetDocument.WorkOrder(document) is null ? null
        : SheetDocument.IndexOf(document, ItemId) >= 0 ? new JsonArray()
        : new JsonArray(Op("add", $"{SheetDocument.ChecklistPath}/-", new JsonObject { ["id"] = ItemId, ["text"] = Text, ["done"] = false }));

    public override string Subject(JsonNode? document) => $"'{Text}'";
}

/// <summary>Adds a note. The same text twice is one note.</summary>
public sealed record AddNote(string Text) : SheetEdit
{
    public override JsonArray? PatchFor(JsonNode? document)
    {
        if (SheetDocument.WorkOrder(document) is null)
        {
            return null;
        }

        var notes = SheetDocument.Notes(document);
        if (notes is null)
        {
            return new JsonArray(Op("add", SheetDocument.NotesPath, new JsonArray(Text)));
        }

        return notes.Any(n => n?.GetValueKind() == System.Text.Json.JsonValueKind.String && n.GetValue<string>() == Text)
            ? new JsonArray()
            : new JsonArray(Op("add", $"{SheetDocument.NotesPath}/-", Text));
    }

    public override string Subject(JsonNode? document) => "a note";
}

/// <summary>Sets the work order's status on the sheet.</summary>
public sealed record SetStatus(string Status) : SheetEdit
{
    public override JsonArray? PatchFor(JsonNode? document) =>
        SheetDocument.WorkOrder(document) is not { } workOrder ? null
        : SheetDocument.Text(workOrder, "status") == Status ? new JsonArray()
        : new JsonArray(Op("replace", $"{SheetDocument.WorkOrderPath}/status", Status));

    public override JsonNode? Target(JsonNode? document) => SheetDocument.WorkOrder(document)?["status"]?.DeepClone();

    public override string Subject(JsonNode? document) => "the status";
}
