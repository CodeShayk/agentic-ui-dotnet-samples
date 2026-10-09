using System.Globalization;
using System.Text.Json;
using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch13.Beacon.Agent;

/// <summary>
/// What the scripted model does when Dana asks for a diagnosis and a service report: plan, work
/// through the plan one step at a time with a lookup per step, then write the report from what the
/// lookups returned. Each model turn's calls are decided from the tool results so far, which makes the
/// run deterministic: the same question always produces the same events.
/// </summary>
/// <remarks>
/// A hosted model given the instructions in <see cref="Instructions"/> follows the same outline, but
/// not the same steps, words or timing. The Desk renders whatever plan and report arrive.
/// </remarks>
public static class DiagnosticScript
{
    /// <summary>Added to Beacon's instructions, for hosted models.</summary>
    public const string Instructions =
        """

        When you're asked to diagnose a work order and draft its service report:
        - First call set_diagnostic_plan with three to six short steps.
        - Mark a step in_progress with update_plan_step before you start it, and done with a one-line note when it's finished.
        - When the investigation is done, write the report with write_service_report. Plain sentences, no Markdown.
        - Don't repeat the plan or the report in your answer. Finish with two sentences at most.
        """;

    private static readonly string[] Steps =
        ["Review the work order", "Read the fault history", "Check warranty and parts", "Draft the service report"];

    public static IReadOnlyList<ScriptRule> Rules { get; } =
    [
        new("diagnose-and-report", Reply: Plan, Summarize: Continue),
    ];

    // Turn 1: the plan, nothing else.
    private static ScriptReply? Plan(ScriptContext ctx) =>
        ctx.Mentions("report") && ctx.Find(BeaconScripts.WorkOrderPattern) is { } workOrderId && ctx.HasTool(DiagnosticTools.SetPlanName)
            ? ScriptReply.Call(DiagnosticTools.SetPlanName, new Dictionary<string, object?> { ["workOrderId"] = workOrderId, ["steps"] = Steps })
            : null;

    // Every later turn: decided by which lookup just answered.
    private static ScriptReply? Continue(ScriptContext ctx)
    {
        var turn = Results(ctx);
        if (!turn.TryGetValue(DiagnosticTools.SetPlanName, out _) || Argument(ctx, DiagnosticTools.SetPlanName, "workOrderId") is not { } workOrderId)
        {
            return null; // not a diagnosis
        }

        var justAnswered = ctx.ToolResults.Select(ctx.ToolNameFor).OfType<string>().ToHashSet();

        if (justAnswered.Contains(DiagnosticTools.SetPlanName))
        {
            return Calls(Step(0, DiagnosticPlan.InProgress), (BeaconTools.GetWorkOrderName, new() { ["workOrderId"] = workOrderId }));
        }

        if (justAnswered.Contains(BeaconTools.GetWorkOrderName))
        {
            var details = turn[BeaconTools.GetWorkOrderName];
            var wo = details.GetProperty("workOrder");
            var equipmentId = details.GetProperty("equipment").GetProperty("id").GetString();
            return Calls(
                Step(0, DiagnosticPlan.Done, $"{wo.GetProperty("status").GetString()}, {wo.GetProperty("priority").GetString()} priority, assigned to {wo.GetProperty("assignedTechnicianId").GetString()}"),
                Step(1, DiagnosticPlan.InProgress),
                (BeaconTools.GetFaultHistoryName, new() { ["equipmentId"] = equipmentId }));
        }

        if (justAnswered.Contains(BeaconTools.GetFaultHistoryName))
        {
            var faults = turn[BeaconTools.GetFaultHistoryName];
            var equipment = turn[BeaconTools.GetWorkOrderName].GetProperty("equipment");
            var latest = faults[faults.GetArrayLength() - 1];
            return Calls(
                Step(1, DiagnosticPlan.Done, $"{faults.GetArrayLength()} faults; latest {latest.GetProperty("code").GetString()} on {latest.GetProperty("date").GetString()}"),
                Step(2, DiagnosticPlan.InProgress),
                (BeaconTools.CheckWarrantyName, new() { ["equipmentId"] = equipment.GetProperty("id").GetString() }),
                (BeaconTools.SearchPartsName, new() { ["query"] = "capacitor", ["model"] = equipment.GetProperty("model").GetString() }));
        }

        if (justAnswered.Contains(BeaconTools.SearchPartsName))
        {
            var warranty = turn[BeaconTools.CheckWarrantyName];
            var part = turn[BeaconTools.SearchPartsName][0];
            return Calls(
                Step(2, DiagnosticPlan.Done, $"Parts covered until {warranty.GetProperty("expiresOn").GetString()}; {part.GetProperty("sku").GetString()} in stock"),
                Step(3, DiagnosticPlan.InProgress));
        }

        if (justAnswered.Contains(DiagnosticTools.UpdatePlanStepName) && !turn.ContainsKey(DiagnosticTools.WriteReportName))
        {
            // The report gets a turn of its own, so nothing else in the turn holds its arguments back.
            return Calls((DiagnosticTools.WriteReportName, Report(turn, workOrderId)));
        }

        if (justAnswered.Contains(DiagnosticTools.WriteReportName))
        {
            var saved = !turn[DiagnosticTools.WriteReportName].TryGetProperty("error", out _);
            return Calls(Step(3, DiagnosticPlan.Done, saved ? $"Saved to {workOrderId}" : "The work-order system refused the report"));
        }

        if (turn[DiagnosticTools.WriteReportName].TryGetProperty("error", out var refused))
        {
            return ScriptReply.Say($"I drafted the service report for {workOrderId}, but it wasn't saved: {refused.GetString()}");
        }

        var sku = turn[BeaconTools.SearchPartsName][0].GetProperty("sku").GetString();
        return ScriptReply.Say(
            $"The service report for {workOrderId} is drafted and saved to the work order. The likely cause is a weak run " +
            $"capacitor: test it first, and replace it with {sku} if it reads low.");
    }

    // The report, written from what the lookups returned, the way a model would compose it.
    private static Dictionary<string, object?> Report(IReadOnlyDictionary<string, JsonElement> turn, string workOrderId)
    {
        var details = turn[BeaconTools.GetWorkOrderName];
        var equipment = details.GetProperty("equipment");
        var faults = turn[BeaconTools.GetFaultHistoryName];
        var latest = faults[faults.GetArrayLength() - 1];
        var warranty = turn[BeaconTools.CheckWarrantyName];
        var part = turn[BeaconTools.SearchPartsName][0];
        var price = part.GetProperty("unitPrice").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture);

        return new()
        {
            ["workOrderId"] = workOrderId,
            ["summary"] =
                $"The {equipment.GetProperty("manufacturer").GetString()} {equipment.GetProperty("model").GetString()} ({equipment.GetProperty("id").GetString()}) " +
                $"at {details.GetProperty("customer").GetProperty("name").GetString()} is short-cycling: it stops after a few minutes and blows warm air.",
            ["findings"] =
                $"The controller logged {faults.GetArrayLength()} faults: " +
                string.Join(", ", faults.EnumerateArray().Select(f => $"{f.GetProperty("code").GetString()} on {f.GetProperty("date").GetString()}")) +
                $". The latest, {latest.GetProperty("code").GetString()} ({latest.GetProperty("description").GetString()!.ToLowerInvariant()}), fits the symptom. " +
                "A weak run capacitor is the most common cause of overload trips on this model and the cheapest to rule out.",
            ["recommendation"] =
                $"Test the run capacitor first. If it reads more than 6 percent below its rating, replace it with {part.GetProperty("sku").GetString()} " +
                $"({part.GetProperty("name").GetString()}, ${price}, {part.GetProperty("inStock").GetInt32()} in stock in {part.GetProperty("warehouse").GetString()}). " +
                (warranty.GetProperty("covered").GetBoolean()
                    ? $"Parts are under warranty until {warranty.GetProperty("expiresOn").GetString()}; labor is billable. "
                    : "The unit is out of warranty. ") +
                "If the capacitor tests good, measure compressor amperage before quoting a compressor.",
        };
    }

    private static (string Tool, Dictionary<string, object?> Arguments) Step(int index, string status, string? note = null)
    {
        Dictionary<string, object?> arguments = new() { ["index"] = index, ["status"] = status };
        if (note is not null)
        {
            arguments["note"] = note;
        }

        return (DiagnosticTools.UpdatePlanStepName, arguments);
    }

    private static ScriptReply Calls(params (string Tool, Dictionary<string, object?> Arguments)[] calls) =>
        new() { ToolCalls = [.. calls.Select(c => new ScriptToolCall(c.Tool, c.Arguments))] };

    // The latest result of each tool since Dana's last message.
    private static Dictionary<string, JsonElement> Results(ScriptContext ctx)
    {
        var lastUser = ctx.Messages.ToList().FindLastIndex(m => m.Role == ChatRole.User);
        Dictionary<string, JsonElement> results = [];
        foreach (var result in ctx.Messages.Skip(lastUser + 1).SelectMany(m => m.Contents).OfType<FunctionResultContent>())
        {
            if (ctx.ToolNameFor(result) is { } name)
            {
                using var document = JsonDocument.Parse(ScriptedChatClient.ResultJson(result));
                results[name] = document.RootElement.Clone();
            }
        }

        return results;
    }

    private static string? Argument(ScriptContext ctx, string tool, string name)
    {
        var lastUser = ctx.Messages.ToList().FindLastIndex(m => m.Role == ChatRole.User);
        var call = ctx.Messages.Skip(lastUser + 1).SelectMany(m => m.Contents).OfType<FunctionCallContent>().LastOrDefault(c => c.Name == tool);
        return call?.Arguments?.TryGetValue(name, out var value) == true
            ? value switch { string text => text, JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(), _ => null }
            : null;
    }
}
