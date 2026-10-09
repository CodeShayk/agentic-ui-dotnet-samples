using System.Globalization;
using System.Text.Json;
using Beacon.AI;
using Beacon.Domain;
using Ch12.Beacon.Agent.Sheet;
using Microsoft.Extensions.AI;

namespace Ch12.Beacon.Agent;

/// <summary>
/// The scripted model's behaviors for the shared sheet, tried before Beacon's defaults. Like a model,
/// the script reads the sheet and the context from the run's data message; unlike one, it always does the
/// same thing, so every capture and test is repeatable. It makes one sheet change per model call,
/// which puts each change on the wire as soon as its tool returns.
/// </summary>
internal static class SheetScripts
{
    public static IReadOnlyList<ScriptRule> Rules { get; } =
    [
        // "Open WO-1001": put a work order on the sheet.
        new("open-sheet",
            Reply: ctx => ctx.Mentions("open") && ctx.Find(BeaconScripts.WorkOrderPattern) is { } workOrderId
                ? ScriptReply.Call(SheetTools.OpenSheetName, new Dictionary<string, object?> { ["workOrderId"] = workOrderId })
                : null,
            Summarize: ctx => LastTool(ctx) == SheetTools.OpenSheetName ? ScriptReply.Say(DescribeOpened(ctx)) : null),

        // "Priya measured the run capacitor: 38 µF against 45, 15.6% low": tick the inspection, add the
        // replacement and note the reading, one call after another.
        new("record-reading",
            Reply: ctx => IsReading(ctx) && CapacitorItem(ctx) is { } item
                ? Call(SheetTools.SetChecklistItemName, ("itemId", item.Id), ("done", true))
                : null,
            Summarize: ctx => !IsReading(ctx) ? null : LastTool(ctx) switch
            {
                SheetTools.SetChecklistItemName => Call(SheetTools.AddChecklistItemName, ("text", "Replace run capacitor (CAP-45-5)")),
                SheetTools.AddChecklistItemName => Call(SheetTools.AddNoteName, ("text", $"Run capacitor reads {ctx.Find(@"\d+(\.\d+)?%") ?? "well"} below its rating. Replace it before testing the compressor.")),
                SheetTools.AddNoteName => ScriptReply.Say("Done. The capacitor inspection is ticked, a replacement with CAP-45-5 is on the checklist and the reading is in the notes."),
                _ => null,
            }),

        // "Add a return visit on the day selected on my board": the date is context, the change is state.
        new("return-visit",
            Reply: ctx => ctx.Mentions("return visit", "follow-up") && SheetPrompt.ReadSheet(ctx.Messages)?.WorkOrder is not null
                ? Call(SheetTools.AddChecklistItemName, ("text", $"Return visit on {VisitDay(ctx)} to confirm the repair"))
                : null,
            Summarize: ctx => ctx.Mentions("return visit", "follow-up") && LastTool(ctx) == SheetTools.AddChecklistItemName
                ? ScriptReply.Say($"Added a return visit on {VisitDay(ctx)}, the day selected on your board.")
                : null),
    ];

    private static bool IsReading(ScriptContext ctx) =>
        ctx.Mentions("capacitor") && ctx.Mentions("reads", "measured");

    private static ChecklistItem? CapacitorItem(ScriptContext ctx) =>
        SheetPrompt.ReadSheet(ctx.Messages)?.WorkOrder?.Checklist
            .FirstOrDefault(i => !i.Done && i.Text.Contains("capacitor", StringComparison.OrdinalIgnoreCase));

    private static string VisitDay(ScriptContext ctx) =>
        SheetPrompt.ReadContextDate(ctx.Messages) is { } day
            ? day.ToString("dddd, MMMM d", CultureInfo.InvariantCulture)
            : "a day to be agreed";

    private static string? LastTool(ScriptContext ctx) =>
        ctx.ToolResults.Count > 0 ? ctx.ToolNameFor(ctx.ToolResults[^1]) : null;

    private static ScriptReply Call(string tool, params (string Name, object? Value)[] arguments) =>
        ScriptReply.Call(tool, arguments.ToDictionary(a => a.Name, a => a.Value));

    // Describes the sheet the open_work_order_sheet result returned, as a model would after reading it.
    private static string DescribeOpened(ScriptContext ctx)
    {
        var requested = ctx.Find(BeaconScripts.WorkOrderPattern);
        using var result = JsonDocument.Parse(ScriptedChatClient.ResultJson(ctx.ToolResults[^1]));
        if (!result.RootElement.TryGetProperty("workOrder", out var sheet) || sheet.GetProperty("id").GetString() != requested)
        {
            return $"I could not find {requested}, so the sheet is unchanged.";
        }

        var checklist = sheet.GetProperty("checklist").EnumerateArray().ToList();
        var done = checklist.Count(i => i.GetProperty("done").GetBoolean());
        return $"{requested} is on the sheet: {sheet.GetProperty("customer").GetString()}, {sheet.GetProperty("equipment").GetString()}, " +
               $"{checklist.Count} checklist items with {done} done.";
    }
}
