using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.AI;
using Beacon.Domain;
using Ch11.Beacon.Components;
using Microsoft.Extensions.AI;

namespace Ch11.Beacon.Agent;

/// <summary>
/// Chapter 11's rules for the scripted model, tried before Beacon's defaults. After a lookup, the model
/// chooses how to show the answer: one part becomes a card, a schedule a timeline, a fault history a chart,
/// and anything else stays text. Two rules misbehave on purpose, the way real models sometimes do: one
/// invents a part, the other calls a component that doesn't exist.
/// </summary>
/// <remarks>The scripted model matches keywords; a real model makes these choices less predictably.</remarks>
public static class ComponentScripts
{
    public static IReadOnlyList<ScriptRule> Rules { get; } =
    [
        // "Show WO-1001 as an invoice": a component the catalog doesn't have. The scripted model calls it
        // anyway (bypassing the offered-tools check a real model doesn't have either).
        new("invoice-view", Reply: ctx =>
            ctx.Mentions("invoice") && ctx.Find(BeaconScripts.WorkOrderPattern) is { } wo
                ? new ScriptReply
                {
                    ExtraContents =
                    [
                        new FunctionCallContent($"call-{ctx.Messages.Count:D4}-x", "show_invoice_table",
                            Args(new JsonObject { ["workOrderId"] = wo, ["lines"] = new JsonArray(new JsonObject { ["sku"] = "CAP-45-5", ["quantity"] = 1 }) })),
                    ],
                }
                : null),

        // "Show me a card for CAP-99": a card straight from the request. For a SKU Beacon doesn't stock,
        // the model makes the part up, and the server has to catch it.
        new("part-card-by-sku", Reply: ctx =>
            ctx.Mentions("card") && ctx.Find(BeaconScripts.SkuPattern) is { } sku
                ? ScriptReply.Call(BeaconComponents.PartCard.ToolName,
                    SeedData.Parts.FirstOrDefault(p => p.Sku == sku) is { } part
                        ? PartCardArgs(part, ctx.Find(BeaconScripts.WorkOrderPattern))
                        : Args(new JsonObject { ["sku"] = sku, ["name"] = "Run capacitor 99 uF", ["unitPrice"] = 24.00m, ["inStock"] = 6 }))
                : null),

        new("show-the-answer", Summarize: ctx => ctx.ToolResults.Count == 1 ? ShowOrSummarize(ctx, ctx.ToolResults[0]) : null),
    ];

    private static ScriptReply? ShowOrSummarize(ScriptContext ctx, FunctionResultContent result)
    {
        var tool = ctx.ToolNameFor(result);
        var json = ScriptedChatClient.ResultJson(result);

        // A component's verdict comes back: say one sentence about it, or recover from a rejection.
        if (ComponentResult.TryParse(json, out var verdict))
        {
            return verdict switch
            {
                { IsAccepted: true } => ScriptReply.Say(AfterAccepted(verdict)),
                { Component: null } when ctx.Find(BeaconScripts.WorkOrderPattern) is { } wo => ScriptReply.Call(
                    BeaconTools.GetWorkOrderName, Args(new JsonObject { ["workOrderId"] = wo }),
                    $"There's no invoice view on the Desk, so here is {wo} as text."),
                _ => ScriptReply.Say($"I couldn't show that as a {Display(verdict.Component)}: {Reason(verdict)}" +
                    (verdict.Component == BeaconComponents.PartCard.ComponentName ? " Ask me to search for the part and I'll show one Beacon stocks." : string.Empty)),
            };
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return tool switch
        {
            // One part answers the question: a card. Several are a comparison: the default table.
            BeaconTools.SearchPartsName when root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1
                                             && ctx.HasTool(BeaconComponents.PartCard.ToolName) =>
                ScriptReply.Call(BeaconComponents.PartCard.ToolName, PartCardArgs(root[0], ctx.Find(BeaconScripts.WorkOrderPattern))),

            BeaconTools.GetScheduleName when root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                                            && ctx.HasTool(BeaconComponents.ScheduleTimeline.ToolName) =>
                ScriptReply.Call(BeaconComponents.ScheduleTimeline.ToolName, TimelineArgs(root)),

            BeaconTools.GetFaultHistoryName when root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                                                && ctx.HasTool(BeaconComponents.FaultHistoryChart.ToolName) =>
                ScriptReply.Call(BeaconComponents.FaultHistoryChart.ToolName, ChartArgs(root)),

            _ => null, // Beacon's default summary: text
        };
    }

    private static Dictionary<string, object?> PartCardArgs(Part part, string? workOrderId) =>
        PartCardArgs(JsonSerializer.SerializeToElement(part, BeaconJsonContext.Default.Part), workOrderId);

    // The props in the order a model would write them: identity first, so a card has a title early.
    private static Dictionary<string, object?> PartCardArgs(JsonElement part, string? workOrderId)
    {
        var props = new JsonObject
        {
            ["sku"] = part.GetProperty("sku").GetString(),
            ["name"] = part.GetProperty("name").GetString(),
            ["unitPrice"] = part.GetProperty("unitPrice").GetDecimal(),
            ["inStock"] = part.GetProperty("inStock").GetInt32(),
            ["warehouse"] = part.GetProperty("warehouse").GetString(),
        };

        if (workOrderId is not null)
        {
            props["workOrderId"] = workOrderId;
            props["note"] = $"Fits the unit on {workOrderId}; the most likely fix for short-cycling.";
        }

        return Args(props);
    }

    private static Dictionary<string, object?> TimelineArgs(JsonElement slots)
    {
        var lanes = new JsonArray();
        foreach (var lane in slots.EnumerateArray().GroupBy(s => s.GetProperty("technicianId").GetString()))
        {
            var laneSlots = new JsonArray();
            foreach (var slot in lane)
            {
                var item = new JsonObject { ["start"] = Clock(slot, "start"), ["end"] = Clock(slot, "end") };
                if (slot.TryGetProperty("workOrderId", out var wo))
                {
                    item["workOrderId"] = wo.GetString();
                }

                laneSlots.Add(item);
            }

            lanes.Add(new JsonObject { ["technicianId"] = lane.Key, ["slots"] = laneSlots });
        }

        return Args(new JsonObject
        {
            ["date"] = SeedData.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["title"] = "Today's schedule",
            ["lanes"] = lanes,
        });
    }

    private static Dictionary<string, object?> ChartArgs(JsonElement faults)
    {
        var points = new JsonArray();
        foreach (var fault in faults.EnumerateArray())
        {
            points.Add(new JsonObject
            {
                ["date"] = fault.GetProperty("date").GetString(),
                ["code"] = fault.GetProperty("code").GetString(),
                ["description"] = fault.GetProperty("description").GetString(),
            });
        }

        var equipmentId = faults[0].GetProperty("equipmentId").GetString();
        return Args(new JsonObject { ["equipmentId"] = equipmentId, ["title"] = $"{equipmentId} fault history", ["faults"] = points });
    }

    private static string AfterAccepted(ComponentResult verdict)
    {
        var props = verdict.Props!.Value;
        return verdict.Component switch
        {
            "PartCard" =>
                $"{props.GetProperty("sku").GetString()} is in stock: {props.GetProperty("inStock").GetInt32()} at " +
                $"{props.GetProperty("warehouse").GetString()}, ${props.GetProperty("unitPrice").GetDecimal():0.00} each." +
                (verdict.Actions?.Any(a => a.Enabled) == true ? " Use **Order this part** on the card to order one." : string.Empty),
            "ScheduleTimeline" =>
                $"Here's the day for {props.GetProperty("lanes").GetArrayLength()} technicians. Open blocks are free time.",
            "FaultHistoryChart" when props.GetProperty("faults") is var faults && faults[faults.GetArrayLength() - 1] is var last =>
                $"{props.GetProperty("equipmentId").GetString()} has {faults.GetArrayLength()} recorded faults; the latest is " +
                $"{last.GetProperty("code").GetString()} ({last.GetProperty("description").GetString()}) on {last.GetProperty("date").GetString()}.",
            _ => "Done.",
        };
    }

    private static string Display(string? component) => component switch
    {
        "PartCard" => "part card",
        "ScheduleTimeline" => "schedule timeline",
        "FaultHistoryChart" => "fault history chart",
        _ => "component",
    };

    // The first sentence of the server's own words, minus the location prefix it writes for the model.
    private static string Reason(ComponentResult verdict)
    {
        if (verdict.Errors is not [var first, ..] || first.IndexOf(": ", StringComparison.Ordinal) is not (var colon and >= 0))
        {
            return "it didn't pass Beacon's checks.";
        }

        var reason = first[(colon + 2)..];
        return reason.IndexOf(". ", StringComparison.Ordinal) is var end and >= 0 ? reason[..(end + 1)] : reason;
    }

    private static string Clock(JsonElement slot, string property) =>
        DateTimeOffset.Parse(slot.GetProperty(property).GetString()!, CultureInfo.InvariantCulture).ToString("HH:mm", CultureInfo.InvariantCulture);

    // Tool-call arguments as a provider delivers them: JSON values, in the order they were written.
    private static Dictionary<string, object?> Args(JsonObject json)
    {
        using var document = JsonDocument.Parse(json.ToJsonString());
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }
}
