using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Beacon.Client.Core;

namespace Ch09.Beacon.ToolCards;

/// <summary>
/// Turns a tool call from the conversation's view state into what a person sees: a headline for the
/// status chip and, once a result arrives, a typed body for a card.
/// </summary>
/// <remarks>
/// Beacon's tools answer with JSON, and they report failure in the same channel as success, as
/// <c>{"error": "..."}</c>, because <c>TOOL_CALL_RESULT</c> has no error field. Results are untrusted:
/// a card reads only the fields it shows, checks their types and treats anything else as absent.
/// </remarks>
public static class ToolCards
{
    /// <summary>The most rows a parts card shows; the rest are counted, not rendered.</summary>
    public const int MaxPartRows = 12;

    // ToolCallView is immutable, so a card computed for one instance stays right for it. The reducer
    // replaces only the calls that changed, so most renders reuse the previous cards.
    private static readonly ConditionalWeakTable<ToolCallView, ToolCard> Cache = [];

    public static ToolCard From(ToolCallView call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return Cache.GetValue(call, Create);
    }

    private static ToolCard Create(ToolCallView call)
    {
        // Complete arguments once TOOL_CALL_END has arrived; until then, whatever the prefix holds.
        var arguments = call.Arguments is { ValueKind: JsonValueKind.Object } complete
            ? PartialArguments.Read(complete.GetRawText())
            : PartialArguments.Read(call.ArgumentsText);

        var (doing, done, subject) = Describe(call.Name, arguments);

        if (call.Status is not ToolCallStatus.Completed)
        {
            var (state, headline) = call.Status switch
            {
                ToolCallStatus.Streaming => (ToolCardState.Preparing, $"{doing} {subject}…"),
                ToolCallStatus.InProgress => (ToolCardState.Running, $"{doing} {subject}…"),
                ToolCallStatus.AwaitingApproval or ToolCallStatus.AwaitingClient => (ToolCardState.Waiting, $"{doing} {subject}: waiting"),
                _ => (ToolCardState.Stopped, $"{doing} {subject}: stopped before it finished"),
            };

            return new ToolCard(call.Id, call.Name, state, headline);
        }

        if (ReadError(call) is { } error)
        {
            return new ToolCard(call.Id, call.Name, ToolCardState.Failed, $"{doing} {subject} failed") { Error = error };
        }

        return new ToolCard(call.Id, call.Name, ToolCardState.Succeeded, $"{done} {subject}") { Body = ReadBody(call) };
    }

    // The verb while running, the verb when done, and what the call is about, from its arguments.
    private static (string Doing, string Done, string Subject) Describe(string tool, IReadOnlyDictionary<string, string> arguments)
    {
        var workOrder = arguments.GetValueOrDefault("workOrderId") is { Length: > 0 } wo ? wo : "a work order";
        var equipment = arguments.GetValueOrDefault("equipmentId") is { Length: > 0 } eq ? eq : "the unit";

        return tool switch
        {
            "get_work_order" => ("Looking up", "Looked up", workOrder),
            "list_work_orders" => ("Listing", "Listed", "work orders"),
            "search_parts" => ("Searching", "Searched", PartsSubject(arguments)),
            "check_warranty" => ("Checking", "Checked", $"the warranty on {equipment}"),
            "get_fault_history" => ("Reading", "Read", $"the fault history of {equipment}"),
            "get_schedule" => ("Reading", "Read", arguments.GetValueOrDefault("technicianId") is { Length: > 0 } tech ? $"the schedule for {tech}" : "today's schedule"),
            "set_checklist_item" => ("Updating", "Updated", $"{arguments.GetValueOrDefault("itemId") ?? "a checklist item"} on {workOrder}"),
            _ => ("Running", "Ran", tool),
        };
    }

    private static string PartsSubject(IReadOnlyDictionary<string, string> arguments)
    {
        var query = arguments.GetValueOrDefault("query");
        var model = arguments.GetValueOrDefault("model");
        return (string.IsNullOrWhiteSpace(query), string.IsNullOrWhiteSpace(model)) switch
        {
            (false, false) => $"parts for “{query}” that fit the {model}",
            (false, true) => $"parts for “{query}”",
            (true, false) => $"parts that fit the {model}",
            _ => "parts",
        };
    }

    // A failure is either an error the client's own tool threw or a result shaped {"error": "..."}.
    private static string? ReadError(ToolCallView call)
    {
        if (!string.IsNullOrEmpty(call.Error))
        {
            return call.Error;
        }

        return TryParse(call.Result) is { ValueKind: JsonValueKind.Object } result
            && result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
    }

    private static ToolResultBody? ReadBody(ToolCallView call)
    {
        if (TryParse(call.Result) is not { } result)
        {
            return string.IsNullOrWhiteSpace(call.Result) ? null : new TextBody(call.Result);
        }

        return call.Name switch
        {
            "get_work_order" => ReadWorkOrder(result),
            "search_parts" => ReadParts(result),
            "check_warranty" => ReadWarranty(result),
            "set_checklist_item" => ReadChecklist(result),
            _ => null, // the answer's text covers the rest
        };
    }

    private static WorkOrderBody? ReadWorkOrder(JsonElement result)
    {
        if (!result.TryGetProperty("workOrder", out var order) || order.ValueKind != JsonValueKind.Object
            || String(order, "id") is not { } id)
        {
            return null;
        }

        var customer = result.TryGetProperty("customer", out var c) ? String(c, "name") : null;
        var equipment = result.TryGetProperty("equipment", out var e) ? e : default;
        var checklist = order.TryGetProperty("checklist", out var items) && items.ValueKind == JsonValueKind.Array ? items : default;

        return new WorkOrderBody(
            id,
            String(order, "status") ?? "Unknown",
            String(order, "priority") ?? "Unknown",
            String(order, "summary") ?? string.Empty,
            customer ?? "Unknown customer",
            String(order, "equipmentId") ?? string.Empty,
            equipment.ValueKind == JsonValueKind.Object ? $"{String(equipment, "manufacturer")} {String(equipment, "model")}".Trim() : string.Empty,
            order.TryGetProperty("scheduledFor", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var start) ? start : null,
            String(order, "assignedTechnicianId"),
            checklist.ValueKind == JsonValueKind.Array ? checklist.EnumerateArray().Count(i => i.ValueKind == JsonValueKind.Object && Boolean(i, "done") == true) : 0,
            checklist.ValueKind == JsonValueKind.Array ? checklist.GetArrayLength() : 0);
    }

    private static PartsBody? ReadParts(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<PartRow> rows = [];
        foreach (var part in result.EnumerateArray().Take(MaxPartRows))
        {
            if (part.ValueKind == JsonValueKind.Object && String(part, "sku") is { } sku
                && part.TryGetProperty("unitPrice", out var price) && price.TryGetDecimal(out var unitPrice)
                && part.TryGetProperty("inStock", out var stock) && stock.TryGetInt32(out var inStock))
            {
                rows.Add(new PartRow(sku, String(part, "name") ?? sku, unitPrice, inStock, String(part, "warehouse") ?? string.Empty));
            }
        }

        return new PartsBody(rows, result.GetArrayLength());
    }

    private static WarrantyBody? ReadWarranty(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && String(result, "equipmentId") is { } equipmentId && Boolean(result, "covered") is { } covered
            ? new WarrantyBody(
                equipmentId,
                covered,
                String(result, "coverage") ?? string.Empty,
                DateOnly.TryParseExact(String(result, "expiresOn"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires) ? expires : null)
            : null;

    private static ChecklistBody? ReadChecklist(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object
        && String(result, "workOrderId") is { } workOrderId && String(result, "itemId") is { } itemId
        && Boolean(result, "done") is { } done && Boolean(result, "changed") is { } changed
        && result.TryGetProperty("doneCount", out var doneCount) && doneCount.TryGetInt32(out var doneItems)
        && result.TryGetProperty("itemCount", out var itemCount) && itemCount.TryGetInt32(out var items)
            ? new ChecklistBody(workOrderId, itemId, String(result, "text") ?? string.Empty, done, changed, doneItems, items)
            : null;

    private static JsonElement? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Boolean(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
