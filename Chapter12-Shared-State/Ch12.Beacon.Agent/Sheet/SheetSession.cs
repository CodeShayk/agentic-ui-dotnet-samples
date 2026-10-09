using Beacon.Domain;

namespace Ch12.Beacon.Agent.Sheet;

/// <summary>
/// The sheet as one run sees it. It starts from the client's state, reconciled, and applies the
/// agent's changes in order, so the server always knows the document its next patch applies to and
/// the snapshot that settles the run. Each change returns what goes on the wire: a whole sheet for a
/// snapshot, or the JSON Patch that describes the change for a delta.
/// </summary>
internal sealed class SheetSession(SheetState start, IFieldServiceStore store)
{
    private const string ChecklistPath = "/workOrder/checklist";
    private readonly Lock _gate = new();

    /// <summary>The sheet after every change the agent made so far.</summary>
    public SheetState Current { get; private set; } = start;

    /// <summary>True when the agent sent deltas since its last snapshot, so the client may have skipped one.</summary>
    public bool NeedsSettling { get; private set; }

    /// <summary>Puts a work order on the sheet. An unknown ID leaves the sheet as it was.</summary>
    public SheetState Open(string workOrderId)
    {
        lock (_gate)
        {
            if (WorkOrderSheets.Project(store, workOrderId) is { } sheet)
            {
                Current = new SheetState { SchemaVersion = SheetState.CurrentVersion, WorkOrder = sheet };
                NeedsSettling = false; // a snapshot is its own baseline
            }

            return Current;
        }
    }

    /// <summary>Ticks or clears a checklist item. Returns an empty patch when there's nothing to change.</summary>
    public IReadOnlyList<PatchOperation> SetItemDone(string itemId, bool done)
    {
        lock (_gate)
        {
            if (Current.WorkOrder is not { } sheet)
            {
                return [];
            }

            var index = IndexOf(sheet, itemId);
            if (index < 0 || sheet.Checklist[index].Done == done)
            {
                return [];
            }

            Change(sheet with { Checklist = [.. sheet.Checklist.Select((item, i) => i == index ? item with { Done = done } : item)] });

            // Indexes move when someone else inserts or removes an item, so the patch pins the item's
            // identity first. If the dispatcher's copy has a different item at this index, the test
            // fails and the whole patch is rejected instead of ticking the wrong line.
            var item = $"{ChecklistPath}/{index}";
            return
            [
                PatchOperation.Test($"{item}/id", itemId, SheetJsonContext.Default.String),
                PatchOperation.Replace($"{item}/done", done, SheetJsonContext.Default.Boolean),
            ];
        }
    }

    /// <summary>Appends a checklist item with an ID minted in the agent's own namespace (B1, B2, ...).</summary>
    public IReadOnlyList<PatchOperation> AddItem(string text)
    {
        lock (_gate)
        {
            if (Current.WorkOrder is not { } sheet || string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            // The dispatcher mints D1, D2...; the seed data uses C1, C2... Neither side can collide with the other.
            var number = 1;
            while (IndexOf(sheet, $"B{number}") >= 0)
            {
                number++;
            }

            var added = new ChecklistItem($"B{number}", text.Trim(), Done: false);
            Change(sheet with { Checklist = [.. sheet.Checklist, added] });

            // "-" appends to whatever the client's array holds, so a concurrent edit can't misplace it.
            return [PatchOperation.Add($"{ChecklistPath}/-", added, SheetJsonContext.Default.ChecklistItem)];
        }
    }

    /// <summary>Appends a note.</summary>
    public IReadOnlyList<PatchOperation> AddNote(string text)
    {
        lock (_gate)
        {
            if (Current.WorkOrder is not { } sheet || string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            Change(sheet with { Notes = [.. sheet.Notes, text.Trim()] });
            return [PatchOperation.Add("/workOrder/notes/-", text.Trim(), SheetJsonContext.Default.String)];
        }
    }

    private void Change(WorkOrderSheet sheet)
    {
        Current = Current with { WorkOrder = sheet };
        NeedsSettling = true;
    }

    private static int IndexOf(WorkOrderSheet sheet, string itemId)
    {
        for (var i = 0; i < sheet.Checklist.Count; i++)
        {
            if (string.Equals(sheet.Checklist[i].Id, itemId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
