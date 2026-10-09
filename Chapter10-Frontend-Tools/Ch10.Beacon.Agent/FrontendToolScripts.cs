using System.Globalization;
using System.Text;
using System.Text.Json;
using Beacon.AI;
using Beacon.Domain;
using Ch10.Beacon.Agent.FrontendTools;
using Microsoft.Extensions.AI;

namespace Ch10.Beacon.Agent;

/// <summary>
/// Chapter 10's behaviors for the scripted model, tried before <see cref="BeaconScripts.Default"/>. They
/// call the clients' tools deterministically, alone or together with Beacon's own tools, so every run in
/// the chapter can be reproduced. The scripted model only calls tools the request offers: when a client
/// didn't declare a tool, the rule falls through, as a real model would simply not see it.
/// </summary>
public static class FrontendToolScripts
{
    public static IReadOnlyList<ScriptRule> Rules { get; } =
    [
        // A frontend tool and a backend tool in one response: "Am I at the right site for WO-1001?"
        new("check-in", Reply: ctx =>
            ctx.Mentions("am i at", "right site", "check me in", "check in") && ctx.Find(BeaconScripts.WorkOrderPattern) is { } wo
                ? new ScriptReply
                {
                    ToolCalls =
                    [
                        new(FrontendToolNames.GetLocation, new Dictionary<string, object?>()),
                        new(BeaconTools.GetWorkOrderName, new Dictionary<string, object?> { ["workOrderId"] = wo }),
                    ],
                }
                : null),

        new("where-am-i", Reply: ctx =>
            ctx.Mentions("where am i", "my location", "my position")
                ? ScriptReply.Call(FrontendToolNames.GetLocation, new Dictionary<string, object?>())
                : null),

        new("take-photo", Reply: ctx =>
            ctx.Mentions("photo", "picture") && ctx.Find(BeaconScripts.WorkOrderPattern) is { } wo
                ? ScriptReply.Call(FrontendToolNames.TakePhoto, new Dictionary<string, object?>
                {
                    ["workOrderId"] = wo,
                    ["subject"] = ctx.Mentions("label") ? "part-label" : ctx.Mentions("damage") ? "damage" : "nameplate",
                })
                : null),

        // The first half of a sequential mixed turn: the label first, the stock check once it's read.
        new("scan-label", Reply: ctx =>
            ctx.Mentions("scan", "barcode", "label")
                ? ScriptReply.Call(FrontendToolNames.ScanPartLabel, ctx.Find(BeaconScripts.WorkOrderPattern) is { } wo
                    ? new Dictionary<string, object?> { ["workOrderId"] = wo }
                    : new Dictionary<string, object?>())
                : null),

        // The Desk's mixed turn: open the work order on Dana's screen and check its warranty in Beacon.
        new("open-and-check-warranty", Reply: ctx =>
            OpensScreen(ctx) && ctx.Mentions("warranty") && ctx.Find(BeaconScripts.WorkOrderPattern) is { } wo
                && SeedData.WorkOrders.FirstOrDefault(w => w.Id == wo) is { } workOrder
                ? new ScriptReply
                {
                    ToolCalls =
                    [
                        new(FrontendToolNames.OpenWorkOrder, new Dictionary<string, object?> { ["workOrderId"] = wo }),
                        new(BeaconTools.CheckWarrantyName, new Dictionary<string, object?> { ["equipmentId"] = workOrder.EquipmentId }),
                    ],
                }
                : null),

        new("open-work-order", Reply: ctx =>
            OpensScreen(ctx) && ctx.Find(BeaconScripts.WorkOrderPattern) is { } wo
                ? ScriptReply.Call(FrontendToolNames.OpenWorkOrder, new Dictionary<string, object?> { ["workOrderId"] = wo })
                : null),

        // The first half of the other order: read the schedule in Beacon, then highlight a slot on the Desk.
        new("highlight-slot", Reply: ctx =>
            ctx.Mentions("highlight") && TechnicianFor(ctx) is { } technician
                ? ScriptReply.Call(BeaconTools.GetScheduleName, new Dictionary<string, object?> { ["technicianId"] = technician.Id })
                : null),

        new("prefill-part-order", Reply: ctx =>
            ctx.Mentions("prefill", "draft", "fill in", "fill out")
                && ctx.Find(BeaconScripts.WorkOrderPattern) is { } wo && ctx.Find(BeaconScripts.SkuPattern) is { } sku
                ? ScriptReply.Call(FrontendToolNames.PrefillPartOrder, new Dictionary<string, object?>
                {
                    ["workOrderId"] = wo,
                    ["sku"] = sku,
                    ["quantity"] = ctx.Find(@"\b([1-9]|10)\b(?= ?x\b)") is { } count ? int.Parse(count, CultureInfo.InvariantCulture) : 1,
                })
                : null),

        // Second halves and summaries.
        new("scan-then-search", Summarize: ctx =>
            ResultOf(ctx, FrontendToolNames.ScanPartLabel) is { } label
                && Text(label, "status") == "read" && Text(label, "sku") is { } sku
                && ctx.HasTool(BeaconTools.SearchPartsName)
                ? ScriptReply.Call(BeaconTools.SearchPartsName, new Dictionary<string, object?> { ["query"] = sku }, $"The label reads {sku}. Checking stock.")
                : null),

        new("schedule-then-highlight", Summarize: ctx =>
            ctx.Mentions("highlight") && ctx.HasTool(FrontendToolNames.HighlightScheduleSlot)
                && ResultOf(ctx, BeaconTools.GetScheduleName) is { ValueKind: JsonValueKind.Array } slots
                && slots.EnumerateArray().OrderByDescending(s => s.TryGetProperty("workOrderId", out _)).FirstOrDefault() is { ValueKind: JsonValueKind.Object } slot
                ? ScriptReply.Call(FrontendToolNames.HighlightScheduleSlot, new Dictionary<string, object?>
                {
                    ["technicianId"] = slot.GetProperty("technicianId").GetString(),
                    ["start"] = slot.GetProperty("start").GetString(),
                })
                : null),

        new("frontend-summary", Summarize: ctx =>
            FrontendResultsThisTurn(ctx).Count > 0 ? ScriptReply.Say(Summarize(ctx)) : null),
    ];

    private static bool OpensScreen(ScriptContext ctx) => ctx.Mentions("open", "pull up", "bring up", "show me");

    private static bool IsFrontend(string? toolName) => toolName is not null && FrontendToolContracts.Beacon.Contains(toolName);

    private static Technician? TechnicianFor(ScriptContext ctx) =>
        ctx.Find(@"TECH-\d") is { } id
            ? SeedData.Technicians.FirstOrDefault(t => t.Id == id)
            : SeedData.Technicians.FirstOrDefault(t => ctx.Mentions(t.Name.Split(' ')[0]));

    // The clients' results from the whole turn, then Beacon's own latest results. A mixed turn spans two
    // runs, and the frontend result is no longer the last thing in the history by the time Beacon answers.
    private static string Summarize(ScriptContext ctx)
    {
        var frontend = FrontendResultsThisTurn(ctx);
        var backend = ctx.ToolResults.Where(r => !IsFrontend(ctx.ToolNameFor(r))).ToList();

        if (frontend.FirstOrDefault(r => ctx.ToolNameFor(r) == FrontendToolNames.GetLocation) is { } location
            && Parse(location) is var here && Text(here, "status") == "located"
            && ResultOf(ctx, BeaconTools.GetWorkOrderName) is { } job && job.TryGetProperty("customer", out var customer))
        {
            return CheckIn(here, job, customer);
        }

        var text = new StringBuilder();
        foreach (var result in frontend)
        {
            text.Append(Describe(ctx.ToolNameFor(result)!, Parse(result))).Append('\n');
        }

        if (backend.Count > 0)
        {
            // Let the default summary describe Beacon's own results, as if they had arrived alone.
            var lastCall = ctx.Messages.ToList().FindLastIndex(m => m.Contents.OfType<FunctionCallContent>().Any());
            var alone = new ScriptContext([.. ctx.Messages.Take(lastCall + 1), new ChatMessage(ChatRole.Tool, [.. backend])], ctx.Options);
            text.Append(BeaconScripts.DefaultSummary(alone).Text).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    // The clients' results since the user's message that the model hasn't talked about yet.
    private static List<FunctionResultContent> FrontendResultsThisTurn(ScriptContext ctx)
    {
        var turnStart = ctx.Messages.ToList().FindLastIndex(m => m.Role == ChatRole.User && m.Contents.OfType<TextContent>().Any());
        List<FunctionResultContent> results = [];
        foreach (var message in ctx.Messages.Skip(turnStart + 1))
        {
            if (message.Role == ChatRole.Assistant && message.Text.Length > 0)
            {
                results.Clear();
            }

            results.AddRange(message.Contents.OfType<FunctionResultContent>().Where(r => IsFrontend(ctx.ToolNameFor(r))));
        }

        return results;
    }

    private static string Describe(string toolName, JsonElement result)
    {
        if (Text(result, "error") is { } error)
        {
            return $"I couldn't use what your app sent back: {error}";
        }

        var status = Text(result, "status");
        return (toolName, status) switch
        {
            (FrontendToolNames.OpenWorkOrder, "opened") => $"**{Text(result, "workOrderId")}** is open on your screen.",
            (FrontendToolNames.OpenWorkOrder, _) => "I couldn't find that work order on the Desk.",

            (FrontendToolNames.HighlightScheduleSlot, "highlighted") =>
                $"I've highlighted {NameOf(Text(result, "technicianId"))}'s {Time(Text(result, "start"))} slot on the schedule.",
            (FrontendToolNames.HighlightScheduleSlot, _) => "That slot isn't on today's schedule.",

            (FrontendToolNames.PrefillPartOrder, "prefilled") =>
                $"The part order form now holds {result.GetProperty("quantity").GetInt32()} x {Text(result, "sku")} for {Text(result, "workOrderId")}. " +
                "Check it and save the draft when you're happy with it.",
            (FrontendToolNames.PrefillPartOrder, "declined") => "All right, I left the part order form as it was.",
            (FrontendToolNames.PrefillPartOrder, "no-answer") => "Nobody answered the confirmation, so the part order form wasn't changed.",
            (FrontendToolNames.PrefillPartOrder, _) => "That work order or part doesn't exist, so I didn't touch the form.",

            (FrontendToolNames.GetLocation, "located") =>
                $"You're at {result.GetProperty("latitude").GetDouble():0.0000}, {result.GetProperty("longitude").GetDouble():0.0000}" +
                (result.TryGetProperty("accuracyMeters", out var accuracy) ? $" (within {accuracy.GetDouble():0} m)." : "."),
            (FrontendToolNames.GetLocation, "declined") => "You chose not to share your location, so I can't confirm where you are.",
            (FrontendToolNames.GetLocation, "permission-denied") => "Beacon Field doesn't have location permission on this device. You can allow it in Settings.",
            (FrontendToolNames.GetLocation, _) => "Your location isn't available right now.",

            (FrontendToolNames.TakePhoto, "captured") => $"Photo {Text(result, "photoId")} is attached to {Text(result, "workOrderId") ?? "the job"}.",
            (FrontendToolNames.TakePhoto, "permission-denied") => "Beacon Field doesn't have camera permission on this device. You can allow it in Settings.",
            (FrontendToolNames.TakePhoto, "unavailable") => "This device has no camera Beacon Field can use.",
            (FrontendToolNames.TakePhoto, _) => "No photo was taken.",

            (FrontendToolNames.ScanPartLabel, "read") => $"The label reads {Text(result, "sku")}.",
            (FrontendToolNames.ScanPartLabel, "unreadable") => "That didn't look like a Contoso part number, such as CAP-45-5.",
            (FrontendToolNames.ScanPartLabel, _) => "No label was read.",

            _ => $"The {toolName} tool finished.",
        };
    }

    private static string CheckIn(JsonElement here, JsonElement job, JsonElement customer)
    {
        var meters = Haversine(
            here.GetProperty("latitude").GetDouble(), here.GetProperty("longitude").GetDouble(),
            customer.GetProperty("latitude").GetDouble(), customer.GetProperty("longitude").GetDouble());
        var site = $"{customer.GetProperty("name").GetString()} ({customer.GetProperty("address").GetString()})";
        var id = job.GetProperty("workOrder").GetProperty("id").GetString();

        return meters <= 250
            ? $"You're at {site}, the site for {id} (about {meters:0} m from the address on file)."
            : $"You're about {meters / 1000:0.0} km from {site}, the site for {id}. Check the address before you start.";
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusMeters = 6_371_000;
        static double Radians(double degrees) => degrees * Math.PI / 180;
        var a = Math.Pow(Math.Sin(Radians(lat2 - lat1) / 2), 2)
                + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Pow(Math.Sin(Radians(lon2 - lon1) / 2), 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Sqrt(a));
    }

    private static JsonElement? ResultOf(ScriptContext ctx, string toolName) =>
        ctx.ToolResults.FirstOrDefault(r => ctx.ToolNameFor(r) == toolName) is { } result ? Parse(result) : null;

    private static JsonElement Parse(FunctionResultContent result)
    {
        try
        {
            return JsonDocument.Parse(ScriptedChatClient.ResultJson(result)).RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NameOf(string? technicianId) =>
        SeedData.Technicians.FirstOrDefault(t => t.Id == technicianId)?.Name ?? technicianId ?? "the technician";

    private static string Time(string? start) =>
        DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.ToString("H:mm", CultureInfo.InvariantCulture) : "selected";
}
