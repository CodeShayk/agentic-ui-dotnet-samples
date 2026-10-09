using System.Globalization;
using System.Text;
using System.Text.Json;
using Beacon.AI;
using Beacon.Domain;
using Ch09.Beacon.Agent.Tools;
using Microsoft.Extensions.AI;

namespace Ch09.Beacon.Agent;

/// <summary>
/// This chapter's additions to the scripted model, tried in front of <see cref="BeaconScripts.Default"/>:
/// a briefing that asks for three lookups in one turn, and the checklist write.
/// </summary>
public static class DeskScripts
{
    public static IReadOnlyList<ScriptRule> Rules { get; } =
    [
        new("prepare-work-order", Reply: PrepareWorkOrder, Summarize: SummarizeBriefing),
        new("set-checklist-item", Reply: SetChecklistItem, Summarize: SummarizeChecklistChange),
    ];

    // "Prepare WO-1001 for Priya": the job, the parts that fit the unit and its warranty. The three
    // lookups don't depend on each other, so the model asks for all of them in one turn. (The scripted
    // model knows which unit a work order concerns; a real model usually needs the work order first,
    // unless the request names the unit, as in "...and the warranty on EQ-501".)
    private static ScriptReply? PrepareWorkOrder(ScriptContext context)
    {
        if (!context.Mentions("prepare", "brief", "get ready")
            || context.Find(BeaconScripts.WorkOrderPattern) is not { } workOrderId
            || SeedData.WorkOrders.FirstOrDefault(w => w.Id == workOrderId) is not { } workOrder)
        {
            return null;
        }

        var equipmentId = context.Find(BeaconScripts.EquipmentPattern) ?? workOrder.EquipmentId;
        var model = SeedData.Equipment.FirstOrDefault(e => e.Id == equipmentId)?.Model;

        return new ScriptReply
        {
            ToolCalls =
            [
                new(BeaconTools.GetWorkOrderName, new Dictionary<string, object?> { ["workOrderId"] = workOrderId }),
                new(BeaconTools.SearchPartsName, new Dictionary<string, object?> { ["query"] = "", ["model"] = model }),
                new(BeaconTools.CheckWarrantyName, new Dictionary<string, object?> { ["equipmentId"] = equipmentId }),
            ],
        };
    }

    private static ScriptReply? SummarizeBriefing(ScriptContext context)
    {
        var results = Results(context);
        if (!results.TryGetValue(BeaconTools.GetWorkOrderName, out var job)
            || !results.TryGetValue(BeaconTools.SearchPartsName, out var parts)
            || !results.TryGetValue(BeaconTools.CheckWarrantyName, out var warranty))
        {
            return null;
        }

        var text = new StringBuilder();

        if (Error(job) is { } jobError)
        {
            text.Append(CultureInfo.InvariantCulture, $"I could not load the work order: {jobError} ");
        }
        else
        {
            var workOrder = job.GetProperty("workOrder");
            var technician = SeedData.Technicians.FirstOrDefault(t => t.Id == Text(workOrder, "assignedTechnicianId"))?.Name ?? "nobody yet";
            var when = workOrder.TryGetProperty("scheduledFor", out var at) && at.TryGetDateTimeOffset(out var start)
                ? start.ToString("HH:mm", CultureInfo.InvariantCulture)
                : "an unscheduled time";
            text.Append(CultureInfo.InvariantCulture,
                $"**{Text(workOrder, "id")}** is {Text(workOrder, "status")} for {when} with {technician} at {Text(job.GetProperty("customer"), "name")}. ");
        }

        if (Error(parts) is { } partsError)
        {
            text.Append(CultureInfo.InvariantCulture, $"I could not search the parts: {partsError} ");
        }
        else if (parts.ValueKind == JsonValueKind.Array)
        {
            var count = parts.GetArrayLength();
            var outOfStock = parts.EnumerateArray().Where(p => p.GetProperty("inStock").GetInt32() == 0).Select(p => Text(p, "sku")).ToList();
            text.Append(CultureInfo.InvariantCulture, $"{count} stocked {(count == 1 ? "part fits" : "parts fit")} the unit");
            text.Append(outOfStock.Count == 0 ? ", all in stock. " : $"; {string.Join(", ", outOfStock)} is out of stock. ");
        }

        if (Error(warranty) is { } warrantyError)
        {
            text.Append(CultureInfo.InvariantCulture, $"I could not check the warranty: {warrantyError}");
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{Text(warranty, "equipmentId")} is {(warranty.GetProperty("covered").GetBoolean() ? "under warranty" : "not under warranty")} ({Text(warranty, "coverage")}).");
        }

        return ScriptReply.Say(text.ToString().TrimEnd());
    }

    // "Mark C3 done on WO-1001", "untick C3 on WO-1001".
    private static ScriptReply? SetChecklistItem(ScriptContext context)
    {
        if (!context.Mentions("mark", "tick", "check off")
            || context.Find(BeaconScripts.WorkOrderPattern) is not { } workOrderId
            || context.Find(@"\bC\d{1,2}\b") is not { } itemId)
        {
            return null;
        }

        var done = !context.Mentions("not done", "untick", "undone");
        return ScriptReply.Call(ChecklistTool.Name, new Dictionary<string, object?>
        {
            ["workOrderId"] = workOrderId,
            ["itemId"] = itemId,
            ["done"] = done,
        });
    }

    private static ScriptReply? SummarizeChecklistChange(ScriptContext context)
    {
        if (!Results(context).TryGetValue(ChecklistTool.Name, out var update))
        {
            return null;
        }

        if (Error(update) is { } error)
        {
            return ScriptReply.Say($"That did not work: {error}");
        }

        var item = $"{Text(update, "itemId")} ({Text(update, "text")})";
        var state = update.GetProperty("done").GetBoolean() ? "done" : "not done";
        var progress = $"{update.GetProperty("doneCount").GetInt32()} of {update.GetProperty("itemCount").GetInt32()} items are done.";

        return ScriptReply.Say(update.GetProperty("changed").GetBoolean()
            ? $"Marked {item} {state} on {Text(update, "workOrderId")}. {progress}"
            : $"{item} on {Text(update, "workOrderId")} was already {state}, so nothing changed. {progress}");
    }

    // The latest result of each tool, parsed. Results the model can't read stay out.
    private static Dictionary<string, JsonElement> Results(ScriptContext context)
    {
        Dictionary<string, JsonElement> results = new(StringComparer.Ordinal);
        foreach (var result in context.ToolResults)
        {
            if (context.ToolNameFor(result) is { } name)
            {
                try
                {
                    results[name] = JsonDocument.Parse(ScriptedChatClient.ResultJson(result)).RootElement.Clone();
                }
                catch (JsonException)
                {
                    // Not JSON (FunctionInvokingChatClient's own error text, for example): leave it to the defaults.
                }
            }
        }

        return results;
    }

    private static string? Error(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out var error) ? error.GetString() : null;

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.ToString() : string.Empty;
}
