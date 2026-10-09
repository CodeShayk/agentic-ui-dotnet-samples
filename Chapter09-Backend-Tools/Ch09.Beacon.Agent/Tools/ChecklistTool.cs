using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Beacon.AI;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch09.Beacon.Agent.Tools;

/// <summary>What <c>set_checklist_item</c> returns: the item as it now stands and whether this call changed it.</summary>
public sealed record ChecklistItemUpdate(
    string WorkOrderId,
    string ItemId,
    string Text,
    bool Done,
    bool Changed,
    int DoneCount,
    int ItemCount);

/// <summary>
/// Beacon's one write in this chapter: ticking a work order's checklist item. It's reversible and
/// costs nothing, so it needs no approval (Chapter 14 decides which writes do). It shows the five
/// properties every backend tool should have: narrow, typed, validated, authorized and idempotent.
/// </summary>
public sealed partial class ChecklistTool(IFieldServiceStore store, Func<CurrentUser> currentUser)
{
    public const string Name = "set_checklist_item";

    public AIFunction Create() =>
        AIFunctionFactory.Create(SetChecklistItem, Name, "Marks one checklist item of a work order as done or not done.", ChecklistJson.Options);

    public object SetChecklistItem(
        [Description("The work order ID, for example WO-1001.")] string workOrderId,
        [Description("The checklist item ID, for example C3.")] string itemId,
        [Description("True to mark the item done, false to mark it not done.")] bool done)
    {
        // Validated: the arguments are model output, so check their shape before using them.
        if (!WorkOrderIdFormat().IsMatch(workOrderId) || !ItemIdFormat().IsMatch(itemId))
        {
            return new ToolError("Use a work order ID like WO-1001 and a checklist item ID like C3.");
        }

        if (store.GetWorkOrder(workOrderId) is not { } workOrder)
        {
            return new ToolError($"Work order {workOrderId} was not found.");
        }

        // Authorized: who is asking comes from the server, never from the arguments.
        var user = currentUser();
        if (!user.IsDispatcher && !string.Equals(workOrder.AssignedTechnicianId, user.TechnicianId, StringComparison.OrdinalIgnoreCase))
        {
            return new ToolError($"Only the dispatcher or the technician assigned to {workOrder.Id} can change its checklist.");
        }

        if (workOrder.Status is WorkOrderStatus.Completed or WorkOrderStatus.Cancelled)
        {
            return new ToolError($"{workOrder.Id} is {workOrder.Status}, so its checklist cannot change.");
        }

        var index = workOrder.Checklist.ToList().FindIndex(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return new ToolError($"{workOrder.Id} has no checklist item {itemId}.");
        }

        // Idempotent: the call sets a value rather than toggling it, so a repeat leaves the same state
        // and says that nothing changed.
        var item = workOrder.Checklist[index];
        var changed = item.Done != done;
        if (changed)
        {
            workOrder = store.UpdateWorkOrder(workOrder with
            {
                Checklist = [.. workOrder.Checklist.Select((entry, i) => i == index ? entry with { Done = done } : entry)],
            });
        }

        return new ChecklistItemUpdate(
            workOrder.Id, item.Id, item.Text, done, changed,
            DoneCount: workOrder.Checklist.Count(i => i.Done),
            ItemCount: workOrder.Checklist.Count);
    }

    [GeneratedRegex(@"^WO-\d{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex WorkOrderIdFormat();

    [GeneratedRegex(@"^C\d{1,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex ItemIdFormat();
}

/// <summary>Source-generated serialization for the checklist tool's result (AOT- and trim-safe).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ChecklistItemUpdate))]
public sealed partial class ChecklistJsonContext : JsonSerializerContext;

/// <summary>Beacon's serializer options plus the checklist tool's own types.</summary>
public static class ChecklistJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(BeaconJson.Options);
        options.TypeInfoResolverChain.Insert(0, ChecklistJsonContext.Default);
        options.MakeReadOnly();
        return options;
    }
}
