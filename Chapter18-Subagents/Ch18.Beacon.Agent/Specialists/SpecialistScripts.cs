using System.Globalization;
using System.Text;
using System.Text.Json;
using Beacon.AI;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch18.Beacon.Agent.Specialists;

/// <summary>
/// The scripted model's behaviors for this chapter, tried before <see cref="BeaconScripts.Default"/>. One
/// model serves the coordinator and all three specialists, as one deployment would; the scripts tell the
/// roles apart by the tools each request offers, which is what a real model has to go on too. A real
/// model writes its own briefs and reports. These make every run of the chapter repeatable.
/// </summary>
public static class SpecialistScripts
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    // FunctionInvokingChatClient's result for a declined call starts with this (Chapter 14).
    private const string RejectedPrefix = "Tool call invocation rejected.";

    // A getter, not an initializer: the rules below are static fields initialized in order, after this line.
    public static IReadOnlyList<ScriptRule> Rules => [Delegate, Answer, Parts, Scheduling, Warranty];

    /// <summary>The coordinator splits the dispatcher's request into one task per specialist, all in one turn.</summary>
    public static ScriptRule Delegate { get; } = new("coordinator-delegate", Reply: ctx =>
    {
        if (!ctx.HasTool("ask_parts_specialist")
            || ctx.Find(BeaconScripts.WorkOrderPattern) is not { } workOrderId
            || Job.For(workOrderId) is not { } job)
        {
            return null;
        }

        var everything = ctx.Mentions("plan", "line up", "everything", "sort out");
        var part = PartKeyword(ctx.UserText);
        var order = ctx.Mentions("order");
        List<ScriptToolCall> calls = [];

        if (everything || order || part is not null || ctx.Mentions("parts"))
        {
            calls.Add(Ask("ask_parts_specialist",
                $"{workOrderId}: find the {part ?? "replacement part"} that fits the {job.Equipment.Manufacturer} {job.Equipment.Model} ({job.Equipment.Id})" +
                (order ? " and order one." : " and say whether it's in stock.")));
        }

        if (everything || ctx.Mentions("schedule", "slot", "book", "technician", "when can"))
        {
            calls.Add(Ask("ask_scheduling_specialist",
                $"{workOrderId}: find the earliest free slot today for {job.Technician?.Name ?? "a technician"}, or someone else if there's none."));
        }

        if (everything || ctx.Mentions("warranty", "covered", "coverage"))
        {
            calls.Add(Ask("ask_warranty_specialist",
                $"{workOrderId}: check the warranty on {job.Equipment.Id}" + (part is null ? "." : $", in particular for the {part}.")));
        }

        return calls.Count == 0 ? null : new ScriptReply { ToolCalls = calls };
    });

    /// <summary>The coordinator's answer, one line per specialist report.</summary>
    public static ScriptRule Answer { get; } = new("coordinator-answer", Summarize: ctx =>
    {
        var reports = ctx.ToolResults
            .Where(r => ctx.ToolNameFor(r)?.StartsWith("ask_", StringComparison.Ordinal) == true)
            .Select(r => JsonDocument.Parse(ScriptedChatClient.ResultJson(r)).RootElement)
            .ToList();
        if (reports.Count == 0)
        {
            return null;
        }

        var workOrderId = ctx.Find(BeaconScripts.WorkOrderPattern) ?? "the work order";
        var resumed = reports.Any(r => r.TryGetProperty("resumed", out var value) && value.GetBoolean());
        var text = new StringBuilder(resumed ? $"Update on {workOrderId}:\n\n" : $"Here's what the specialists found for {workOrderId}:\n\n");

        foreach (var report in reports)
        {
            var label = report.GetProperty("label").GetString();
            var line = report.GetProperty("status").GetString() switch
            {
                "waiting_for_approval" => $"{Field(report, "report")} Waiting for your approval: {Field(report, "waitingFor")}",
                "failed" => $"no answer. {Field(report, "error")}",
                _ => Field(report, "report"),
            };
            text.Append(CultureInfo.InvariantCulture, $"- **{label}:** {line}\n");
        }

        if (reports.Any(r => r.GetProperty("status").GetString() == "waiting_for_approval"))
        {
            text.Append("\nApprove or decline the order and the parts specialist will pick up where it stopped.");
        }
        else if (reports.Any(r => r.GetProperty("status").GetString() == "failed"))
        {
            text.Append("\nCheck again before you commit to a plan that depends on the missing answer.");
        }

        return ScriptReply.Say(text.ToString().TrimEnd());
    });

    /// <summary>The parts specialist: search, then order if asked, then report.</summary>
    public static ScriptRule Parts { get; } = new("parts-specialist",
        Reply: ctx =>
            ctx.HasTool(BeaconTools.SearchPartsName) && Job.FromTask(ctx) is { } job
                ? ScriptReply.Call(BeaconTools.SearchPartsName,
                    new Dictionary<string, object?> { ["query"] = PartKeyword(ctx.UserText) ?? string.Empty, ["model"] = job.Equipment.Model },
                    $"Searching stock for a {PartKeyword(ctx.UserText) ?? "part"} that fits the {job.Equipment.Model}.")
                : null,
        Summarize: ctx =>
        {
            if (!ctx.HasTool(BeaconTools.SearchPartsName) || Job.FromTask(ctx) is not { } job || ctx.ToolResults.Count == 0)
            {
                return null;
            }

            var result = ctx.ToolResults[^1];
            var json = ScriptedChatClient.ResultJson(result);

            if (ctx.ToolNameFor(result) == BeaconTools.PlacePartOrderName)
            {
                if (json.StartsWith(RejectedPrefix, StringComparison.Ordinal) || json.StartsWith('"'))
                {
                    var reason = json.Trim('"').Replace(RejectedPrefix, string.Empty, StringComparison.Ordinal).Trim();
                    return ScriptReply.Say($"Nothing was ordered for {job.WorkOrder.Id}. {reason}".TrimEnd());
                }

                var order = JsonDocument.Parse(json).RootElement;
                return order.TryGetProperty("error", out var error)
                    ? ScriptReply.Say($"Nothing was ordered for {job.WorkOrder.Id}: {error.GetString()}")
                    : ScriptReply.Say($"Ordered {order.GetProperty("quantity").GetInt32()} x {order.GetProperty("sku").GetString()} " +
                        $"for {job.WorkOrder.Id} as {order.GetProperty("id").GetString()}, {Money(order.GetProperty("total").GetDecimal())}.");
            }

            var part = JsonDocument.Parse(json).RootElement.EnumerateArray().FirstOrDefault(p => p.GetProperty("inStock").GetInt32() > 0);
            if (part.ValueKind != JsonValueKind.Object)
            {
                return ScriptReply.Say($"Nothing that fits the {job.Equipment.Model} is in stock.");
            }

            var sku = part.GetProperty("sku").GetString()!;
            var found = $"{sku} ({part.GetProperty("name").GetString()}, {Money(part.GetProperty("unitPrice").GetDecimal())}) fits the {job.Equipment.Model}; " +
                $"{part.GetProperty("warehouse").GetString()} has {part.GetProperty("inStock").GetInt32()} in stock.";

            return ctx.Mentions("order") && ctx.HasTool(BeaconTools.PlacePartOrderName)
                ? ScriptReply.Call(BeaconTools.PlacePartOrderName,
                    new Dictionary<string, object?> { ["workOrderId"] = job.WorkOrder.Id, ["sku"] = sku, ["quantity"] = 1 },
                    $"{found} Ordering one for {job.WorkOrder.Id}.")
                : ScriptReply.Say(found);
        });

    /// <summary>The scheduling specialist: the assigned technician's earliest free slot, or whoever is free.</summary>
    public static ScriptRule Scheduling { get; } = new("scheduling-specialist",
        Reply: ctx =>
            ctx.HasTool(BeaconTools.GetScheduleName) && Job.FromTask(ctx) is { } job
                ? ScriptReply.Call(BeaconTools.GetScheduleName, new Dictionary<string, object?>(), $"Checking today's schedule for {job.WorkOrder.Id}.")
                : null,
        Summarize: ctx =>
        {
            if (!ctx.HasTool(BeaconTools.GetScheduleName) || Job.FromTask(ctx) is not { } job || ctx.ToolResults.Count == 0)
            {
                return null;
            }

            var free = JsonDocument.Parse(ScriptedChatClient.ResultJson(ctx.ToolResults[^1])).RootElement.EnumerateArray()
                .Where(s => !s.TryGetProperty("workOrderId", out _))
                .Select(s => (Technician: s.GetProperty("technicianId").GetString()!, Start: Time(s, "start"), End: Time(s, "end")))
                .ToList();

            if (job.Technician is { } assigned && free.FirstOrDefault(s => s.Technician == assigned.Id) is { Technician: not null } own)
            {
                return ScriptReply.Say($"{assigned.Name} ({assigned.Id}) is free from {own.Start} to {own.End} today; book {job.WorkOrder.Id} at {own.Start}.");
            }

            // Otherwise the earliest free technician, from the same region first.
            var region = job.Technician?.Region;
            var other = free.OrderBy(s => SeedData.Technicians.First(t => t.Id == s.Technician).Region == region ? 0 : 1).FirstOrDefault();
            if (other.Technician is not null && SeedData.Technicians.First(t => t.Id == other.Technician) is var someone)
            {
                return ScriptReply.Say($"{job.Technician?.Name ?? "Nobody"} has no free time left today. {someone.Name} ({someone.Id}) is free from {other.Start} to {other.End}.");
            }

            return ScriptReply.Say("Nobody has free time left today.");
        });

    /// <summary>The warranty specialist: one lookup, one sentence.</summary>
    public static ScriptRule Warranty { get; } = new("warranty-specialist",
        Reply: ctx =>
            ctx.HasTool(BeaconTools.CheckWarrantyName) && Job.FromTask(ctx) is { } job
                ? ScriptReply.Call(BeaconTools.CheckWarrantyName, new Dictionary<string, object?> { ["equipmentId"] = job.Equipment.Id },
                    $"Checking the warranty on {job.Equipment.Id}.")
                : null,
        Summarize: ctx =>
        {
            if (!ctx.HasTool(BeaconTools.CheckWarrantyName) || ctx.ToolResults.Count == 0)
            {
                return null;
            }

            var warranty = JsonDocument.Parse(ScriptedChatClient.ResultJson(ctx.ToolResults[^1])).RootElement;
            if (warranty.TryGetProperty("error", out var error))
            {
                return ScriptReply.Say(error.GetString() ?? "There's no warranty record.");
            }

            var equipment = warranty.GetProperty("equipmentId").GetString();
            var coverage = warranty.GetProperty("coverage").GetString();
            return warranty.GetProperty("covered").GetBoolean()
                ? ScriptReply.Say($"{equipment} is under warranty until {DateOnly.Parse(warranty.GetProperty("expiresOn").GetString()!, CultureInfo.InvariantCulture).ToString("MMMM d, yyyy", Us)}: {coverage}.")
                : ScriptReply.Say($"{equipment} isn't under warranty ({coverage}).");
        });

    private static ScriptToolCall Ask(string tool, string task) => new(tool, new Dictionary<string, object?> { ["task"] = task });

    private static string? PartKeyword(string text) =>
        new[] { "compressor", "capacitor", "fan motor", "bearing", "contactor", "expansion tank", "filter", "igniter", "valve" }
            .FirstOrDefault(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static string Field(JsonElement report, string name) =>
        report.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string Money(decimal amount) => amount.ToString("C2", Us);

    private static string Time(JsonElement slot, string property) =>
        DateTimeOffset.Parse(slot.GetProperty(property).GetString()!, CultureInfo.InvariantCulture).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A work order with its equipment and technician, from the seed data the scripts can see.</summary>
    private sealed record Job(WorkOrder WorkOrder, Equipment Equipment, Technician? Technician)
    {
        public static Job? For(string workOrderId) =>
            SeedData.WorkOrders.FirstOrDefault(w => w.Id == workOrderId) is { } workOrder
                ? new Job(workOrder, SeedData.Equipment.First(e => e.Id == workOrder.EquipmentId),
                    SeedData.Technicians.FirstOrDefault(t => t.Id == workOrder.AssignedTechnicianId))
                : null;

        // A specialist's task names its work order, which is all the scripts need to find their way.
        public static Job? FromTask(ScriptContext ctx) =>
            ctx.Find(BeaconScripts.WorkOrderPattern) is { } workOrderId ? For(workOrderId) : null;
    }
}
