using System.Globalization;
using System.Text;
using System.Text.Json;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Beacon.AI;

/// <summary>
/// The scripted model's default behaviors for Beacon. Chapters add their own rules in front of these
/// (rules are tried in order and the first non-null reply wins).
/// </summary>
public static class BeaconScripts
{
    public const string WorkOrderPattern = @"WO-\d{4}";
    public const string EquipmentPattern = @"EQ-\d{3}";
    public const string SkuPattern = @"\b[A-Z]{3}-[A-Z0-9]+(?:-[A-Z0-9]+)?\b";

    public static ScriptReply Fallback { get; } = ScriptReply.Say(
        "I'm Beacon, Contoso Climate's field-service assistant. Ask me about a work order " +
        "(for example WO-1001), parts for a unit, warranty coverage, fault history or today's schedule.");

    public static IReadOnlyList<ScriptRule> Default { get; } =
    [
        new("place-part-order", Reply: ctx =>
            ctx.Mentions("order") && ctx.Find(WorkOrderPattern) is { } wo && ctx.Find(SkuPattern) is { } sku
                ? ScriptReply.Call(BeaconTools.PlacePartOrderName,
                    new Dictionary<string, object?> { ["workOrderId"] = wo, ["sku"] = sku, ["quantity"] = 1 },
                    $"I'll order one {sku} for {wo}.")
                : null),

        new("charge-customer", Reply: ctx =>
            ctx.Mentions("charge", "bill") && ctx.Find(WorkOrderPattern) is { } wo && ctx.Find(@"\$?\d+(?:\.\d{2})?") is { } amount
                ? ScriptReply.Call(BeaconTools.ChargeCustomerName,
                    new Dictionary<string, object?>
                    {
                        ["workOrderId"] = wo,
                        ["amount"] = decimal.Parse(amount.TrimStart('$'), CultureInfo.InvariantCulture),
                        ["description"] = $"Service on {wo}",
                    },
                    $"I'll charge the customer for {wo}.")
                : null),

        new("check-warranty", Reply: ctx =>
            ctx.Mentions("warranty") && EquipmentFor(ctx) is { } eq
                ? ScriptReply.Call(BeaconTools.CheckWarrantyName, new Dictionary<string, object?> { ["equipmentId"] = eq })
                : null),

        new("fault-history", Reply: ctx =>
            ctx.Mentions("fault", "history", "codes") && EquipmentFor(ctx) is { } eq
                ? ScriptReply.Call(BeaconTools.GetFaultHistoryName, new Dictionary<string, object?> { ["equipmentId"] = eq })
                : null),

        new("search-parts", Reply: ctx =>
            PartKeyword(ctx) is { } keyword
                ? ScriptReply.Call(BeaconTools.SearchPartsName,
                    new Dictionary<string, object?> { ["query"] = keyword, ["model"] = ModelFor(ctx) })
                : null),

        new("schedule", Reply: ctx =>
            ctx.Mentions("schedule", "today", "who is free", "availability")
                ? ScriptReply.Call(BeaconTools.GetScheduleName,
                    new Dictionary<string, object?> { ["technicianId"] = ctx.Find(@"TECH-\d") })
                : null),

        new("list-work-orders", Reply: ctx =>
            ctx.Mentions("work orders", "open jobs", "jobs")
                ? ScriptReply.Call(BeaconTools.ListWorkOrdersName, new Dictionary<string, object?>())
                : null),

        new("diagnose", Reply: ctx =>
            ctx.Mentions("diagnose", "why", "cause")
                ? new ScriptReply
                {
                    Reasoning =
                        "The unit short-cycles and blows warm air. Short-cycling with warm air usually points to the " +
                        "compressor tripping on overload. A weak run capacitor is the most common cause and the cheapest " +
                        "to rule out, so check it before condemning the compressor.",
                    Text =
                        "The most likely cause is a failing run capacitor making the compressor trip on overload.\n\n" +
                        "1. Measure the capacitor (CAP-45-5 is rated 45/5 uF).\n" +
                        "2. If it reads more than 6% low, replace it.\n" +
                        "3. If it tests fine, measure compressor amperage before ordering a compressor.",
                }
                : null),

        new("get-work-order", Reply: ctx =>
            ctx.Find(WorkOrderPattern) is { } wo
                ? ScriptReply.Call(BeaconTools.GetWorkOrderName, new Dictionary<string, object?> { ["workOrderId"] = wo })
                : null),

        new("greeting", Reply: ctx =>
            ctx.Mentions("hello", "hi ", "hey", "what can you do", "help")
                ? Fallback
                : null),
    ];

    /// <summary>Turns tool results into a short answer, the way a model would after calling tools.</summary>
    public static ScriptReply DefaultSummary(ScriptContext context)
    {
        var text = new StringBuilder();
        foreach (var result in context.ToolResults)
        {
            var name = context.ToolNameFor(result) ?? "tool";
            text.AppendLine(Describe(name, ScriptedChatClient.ResultJson(result)));
        }

        return ScriptReply.Say(text.ToString().TrimEnd());
    }

    private static string Describe(string toolName, string json)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return $"The {toolName} tool returned: {json}";
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
        {
            return $"That didn't work: {error.GetString()}";
        }

        return toolName switch
        {
            BeaconTools.GetWorkOrderName when root.TryGetProperty("workOrder", out var wo) =>
                $"**{wo.GetProperty("id").GetString()}** is {wo.GetProperty("status").GetString()} " +
                $"({wo.GetProperty("priority").GetString()} priority): {wo.GetProperty("summary").GetString()}. " +
                $"Customer: {root.GetProperty("customer").GetProperty("name").GetString()}; " +
                $"equipment: {root.GetProperty("equipment").GetProperty("manufacturer").GetString()} " +
                $"{root.GetProperty("equipment").GetProperty("model").GetString()}.",

            BeaconTools.SearchPartsName when root.ValueKind == JsonValueKind.Array =>
                root.GetArrayLength() == 0
                    ? "I couldn't find a matching part in stock."
                    : "Here's what's in stock:\n\n| SKU | Part | Price | In stock |\n|---|---|---:|---:|\n" +
                      string.Join('\n', root.EnumerateArray().Select(p =>
                          $"| {p.GetProperty("sku").GetString()} | {p.GetProperty("name").GetString()} | " +
                          $"${p.GetProperty("unitPrice").GetDecimal():0.00} | {p.GetProperty("inStock").GetInt32()} |")),

            BeaconTools.CheckWarrantyName =>
                root.GetProperty("covered").GetBoolean()
                    ? $"{root.GetProperty("equipmentId").GetString()} is under warranty: {root.GetProperty("coverage").GetString()}."
                    : $"{root.GetProperty("equipmentId").GetString()} is not under warranty ({root.GetProperty("coverage").GetString()}).",

            BeaconTools.GetFaultHistoryName when root.ValueKind == JsonValueKind.Array =>
                root.GetArrayLength() == 0
                    ? "No faults are recorded for that unit."
                    : "Recorded faults:\n\n" + string.Join('\n', root.EnumerateArray().Select(f =>
                        $"- {f.GetProperty("date").GetString()}: {f.GetProperty("code").GetString()} ({f.GetProperty("description").GetString()})")),

            BeaconTools.GetScheduleName when root.ValueKind == JsonValueKind.Array =>
                "Today's schedule:\n\n" + string.Join('\n', root.EnumerateArray().Select(s =>
                    $"- {s.GetProperty("technicianId").GetString()}: " +
                    $"{DateTimeOffset.Parse(s.GetProperty("start").GetString()!, CultureInfo.InvariantCulture):HH:mm}-" +
                    $"{DateTimeOffset.Parse(s.GetProperty("end").GetString()!, CultureInfo.InvariantCulture):HH:mm} " +
                    (s.TryGetProperty("workOrderId", out var w) ? w.GetString() : "free"))),

            BeaconTools.ListWorkOrdersName when root.ValueKind == JsonValueKind.Array =>
                "Work orders:\n\n" + string.Join('\n', root.EnumerateArray().Select(w =>
                    $"- {w.GetProperty("id").GetString()} ({w.GetProperty("status").GetString()}): {w.GetProperty("summary").GetString()}")),

            BeaconTools.PlacePartOrderName when root.TryGetProperty("id", out var id) =>
                $"Order {id.GetString()} is placed: {root.GetProperty("quantity").GetInt32()} x {root.GetProperty("sku").GetString()} " +
                $"for ${root.GetProperty("total").GetDecimal():0.00}.",

            BeaconTools.ChargeCustomerName when root.TryGetProperty("id", out var id) =>
                $"Charge {id.GetString()} for ${root.GetProperty("amount").GetDecimal():0.00} is recorded.",

            _ => $"The {toolName} tool returned: {json}",
        };
    }

    private static string? EquipmentFor(ScriptContext ctx) =>
        ctx.Find(EquipmentPattern)
        ?? (ctx.Find(WorkOrderPattern) is { } wo
            ? SeedData.WorkOrders.FirstOrDefault(w => w.Id == wo)?.EquipmentId
            : null);

    private static string? ModelFor(ScriptContext ctx) =>
        EquipmentFor(ctx) is { } eq ? SeedData.Equipment.FirstOrDefault(e => e.Id == eq)?.Model : null;

    private static string? PartKeyword(ScriptContext ctx) =>
        new[] { "capacitor", "compressor", "bearing", "fan motor", "contactor", "filter", "igniter", "expansion tank", "valve" }
            .FirstOrDefault(k => ctx.UserText.Contains(k, StringComparison.OrdinalIgnoreCase))
        ?? (ctx.Mentions("part", "parts") ? string.Empty : null);
}
