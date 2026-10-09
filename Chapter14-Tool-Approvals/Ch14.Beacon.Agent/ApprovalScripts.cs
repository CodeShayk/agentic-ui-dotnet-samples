using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.AI;
using Beacon.Domain;
using Ch14.Beacon.Agent.Approvals;

namespace Ch14.Beacon.Agent;

/// <summary>
/// The scripted model's behaviors for this chapter, tried before <see cref="BeaconScripts.Default"/>.
/// A real model decides for itself what to order; the scripts make the chapter's two cases repeatable:
/// the WO-1001 compressor (CMP-CA36, $1,795.00, over the threshold) and its run capacitor (CAP-45-5,
/// $38.50, under it), plus charges either side of the line.
/// </summary>
public static partial class ApprovalScripts
{
    /// <summary>"Order a replacement compressor for WO-1001": the part that fits the work order's unit.</summary>
    public static ScriptRule CompressorOrder { get; } = new("order-compressor", Reply: ctx =>
        ctx.Mentions("compressor") && ctx.Mentions("order", "replace")
        && ctx.Find(BeaconScripts.WorkOrderPattern) is { } workOrderId
        && PartFor(workOrderId, "compressor") is { } part
            ? ScriptReply.Call(BeaconTools.PlacePartOrderName, Order(workOrderId, part.Sku),
                $"The capacitor checks out, so the compressor is the fault. Ordering a replacement compressor, {part.Sku}, for {workOrderId}.")
            : null);

    /// <summary>"Order a run capacitor for WO-1001".</summary>
    public static ScriptRule CapacitorOrder { get; } = new("order-capacitor", Reply: ctx =>
        ctx.Mentions("capacitor") && ctx.Mentions("order")
        && ctx.Find(BeaconScripts.WorkOrderPattern) is { } workOrderId
        && PartFor(workOrderId, "capacitor") is { } part
            ? ScriptReply.Call(BeaconTools.PlacePartOrderName, Order(workOrderId, part.Sku),
                $"Ordering a run capacitor, {part.Sku}, for {workOrderId}.")
            : null);

    /// <summary>"Charge Harbor View Dental $2,145.00 for WO-1001".</summary>
    public static ScriptRule CustomerCharge { get; } = new("charge-customer", Reply: ctx =>
        ctx.Mentions("charge", "bill")
        && ctx.Find(BeaconScripts.WorkOrderPattern) is { } workOrderId
        && Amount().Match(ctx.UserText) is { Success: true } amount
        && SeedData.WorkOrders.FirstOrDefault(w => w.Id == workOrderId) is { } workOrder
            ? ScriptReply.Call(BeaconTools.ChargeCustomerName,
                new Dictionary<string, object?>
                {
                    ["workOrderId"] = workOrderId,
                    ["amount"] = decimal.Parse(amount.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture),
                    ["description"] = ctx.Mentions("compressor") ? "Compressor replacement, parts and labor" : $"Service visit on {workOrderId}",
                },
                $"Charging {SeedData.Customers.First(c => c.Id == workOrder.CustomerId).Name} for {workOrderId}.")
            : null);

    /// <summary>
    /// After a consequential call comes back refused or declined, say so plainly. Successful results fall
    /// through to the default summary ("Order PO-9001 is placed...").
    /// </summary>
    public static ScriptRule Outcomes { get; } = new("approval-outcomes", Summarize: ctx =>
    {
        List<string> lines = [];
        foreach (var result in ctx.ToolResults)
        {
            var noun = ctx.ToolNameFor(result) switch
            {
                BeaconTools.PlacePartOrderName => "Nothing was ordered",
                BeaconTools.ChargeCustomerName => "The customer was not charged",
                _ => null,
            };
            var text = ScriptedChatClient.ResultJson(result);

            if (noun is null)
            {
                continue;
            }

            if (text.StartsWith(RejectedPrefix, StringComparison.Ordinal))
            {
                lines.Add($"Understood. {noun}. {text[RejectedPrefix.Length..].Trim()}");
            }
            else if (Error(text) is { } error)
            {
                lines.Add($"{noun}: {error}");
            }
        }

        return lines.Count > 0 ? ScriptReply.Say(string.Join('\n', lines)) : null;
    });

    public static IReadOnlyList<ScriptRule> Rules { get; } = [CustomerCharge, CompressorOrder, CapacitorOrder, Outcomes];

    // FunctionInvokingChatClient's result for a declined call starts with this.
    private const string RejectedPrefix = "Tool call invocation rejected.";

    private static Dictionary<string, object?> Order(string workOrderId, string sku) =>
        new() { ["workOrderId"] = workOrderId, ["sku"] = sku, ["quantity"] = 1 };

    // The part of that kind that fits the work order's equipment, from the catalog.
    private static Part? PartFor(string workOrderId, string kind) =>
        SeedData.WorkOrders.FirstOrDefault(w => w.Id == workOrderId) is { } workOrder
        && SeedData.Equipment.FirstOrDefault(e => e.Id == workOrder.EquipmentId) is { } equipment
            ? SeedData.Parts.FirstOrDefault(p => p.Name.Contains(kind, StringComparison.OrdinalIgnoreCase) && p.CompatibleModels.Contains(equipment.Model))
            : null;

    private static string? Error(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("error", out var error)
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\$(\d{1,3}(?:,\d{3})*(?:\.\d{2})?|\d+(?:\.\d{2})?)")]
    private static partial Regex Amount();
}
