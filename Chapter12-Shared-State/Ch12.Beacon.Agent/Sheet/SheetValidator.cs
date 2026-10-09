using System.Text.Json;
using System.Text.RegularExpressions;
using AGUI.Abstractions;
using Beacon.Domain;

namespace Ch12.Beacon.Agent.Sheet;

/// <summary>
/// Checks the parts of a run's input this chapter adds meaning to: <c>state</c> against the sheet's
/// schema and rules, and <c>context</c> against size limits. Errors are keyed by JSON path, the shape
/// ASP.NET Core's validation problem details use.
/// </summary>
internal static partial class SheetValidator
{
    public const int MaxStateLength = 16 * 1024;
    public const int MaxChecklistItems = 40;
    public const int MaxItemLength = 200;
    public const int MaxNotes = 40;
    public const int MaxNoteLength = 500;
    public const int MaxContextEntries = 8;
    public const int MaxContextLength = 500;

    public static Dictionary<string, string[]> Validate(RunAgentInput input, IFieldServiceStore store)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Error(string path, string message)
        {
            if (!errors.TryGetValue(path, out var list))
            {
                errors[path] = list = [];
            }

            list.Add(message);
        }

        if (input.State is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } state)
        {
            if (state.GetRawText().Length > MaxStateLength)
            {
                Error("state", $"The sheet may hold at most {MaxStateLength} characters.");
            }
            else if (Read(state, Error) is { } sheet)
            {
                CheckRules(sheet, store, Error);
            }
        }

        var context = input.Context ?? [];
        if (context.Count > MaxContextEntries)
        {
            Error("context", $"At most {MaxContextEntries} context entries are accepted.");
        }

        for (var i = 0; i < context.Count; i++)
        {
            if (context[i].Description is not { Length: > 0 and <= MaxContextLength } || context[i].Value is not { Length: <= MaxContextLength })
            {
                Error($"context[{i}]", $"A description of 1 to {MaxContextLength} characters and a value of at most {MaxContextLength} are required.");
            }
        }

        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);
    }

    // The schema: SheetState's contract under SheetJsonContext's strict options. The message names the
    // path but not the .NET type, which is the server's business.
    private static SheetState? Read(JsonElement state, Action<string, string> error)
    {
        try
        {
            return JsonSerializer.Deserialize(state, SheetJsonContext.Default.SheetState);
        }
        catch (JsonException ex)
        {
            error("state" + (ex.Path ?? "$")[1..], "This doesn't match the sheet's schema (GET /agent/state-schema).");
            return null;
        }
    }

    // The rules a schema can't express: ranges, formats, uniqueness and references to real work orders.
    private static void CheckRules(SheetState sheet, IFieldServiceStore store, Action<string, string> error)
    {
        if (sheet.SchemaVersion is < 1 or > SheetState.CurrentVersion)
        {
            error("state.schemaVersion", $"Versions 1 to {SheetState.CurrentVersion} are understood.");
        }

        if (sheet.WorkOrder is not { } workOrder)
        {
            return;
        }

        if (!WorkOrderId().IsMatch(workOrder.Id) || store.GetWorkOrder(workOrder.Id) is null)
        {
            error("state.workOrder.id", "Must name an existing work order, such as WO-1001.");
        }

        if (workOrder.Checklist.Count > MaxChecklistItems)
        {
            error("state.workOrder.checklist", $"At most {MaxChecklistItems} items.");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        for (var i = 0; i < workOrder.Checklist.Count; i++)
        {
            // RespectNullableAnnotations doesn't reach collection elements, so [null] gets this far.
            if (workOrder.Checklist[i] is not { } item)
            {
                error($"state.workOrder.checklist[{i}]", "Must be a checklist item, not null.");
                continue;
            }

            if (!ItemId().IsMatch(item.Id) || !ids.Add(item.Id))
            {
                error($"state.workOrder.checklist[{i}].id", "Must be a unique ID such as C1, B1 or D1.");
            }

            if (string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > MaxItemLength)
            {
                error($"state.workOrder.checklist[{i}].text", $"Must be 1 to {MaxItemLength} characters.");
            }
        }

        if (workOrder.Notes.Count > MaxNotes)
        {
            error("state.workOrder.notes", $"At most {MaxNotes} notes.");
        }

        for (var i = 0; i < workOrder.Notes.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(workOrder.Notes[i]) || workOrder.Notes[i].Length > MaxNoteLength)
            {
                error($"state.workOrder.notes[{i}]", $"Must be 1 to {MaxNoteLength} characters.");
            }
        }
    }

    [GeneratedRegex(@"^WO-\d{4}$")]
    private static partial Regex WorkOrderId();

    [GeneratedRegex(@"^[A-Z]\d{1,4}$")]
    private static partial Regex ItemId();
}
