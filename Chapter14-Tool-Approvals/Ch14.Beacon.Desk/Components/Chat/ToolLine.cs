using System.Globalization;
using System.Text.Json;
using Beacon.Client.Core;

namespace Ch14.Beacon.Desk.Components.Chat;

/// <summary>
/// One sentence about a tool call, for the activity line under a message and the approvals log.
/// Chapter 7's version, plus the two tools that spend money.
/// </summary>
/// <remarks>
/// Arguments come from <see cref="ToolCallView.Arguments"/>, parsed once when the call first closed:
/// the proposal. Never from <see cref="ToolCallView.ArgumentsText"/>, because a resumed run restates the
/// whole call under the same ID and the reducer appends the restatement to that text. What actually ran
/// (an edit may have changed it) comes from the result.
/// </remarks>
public static class ToolLine
{
    public static string Describe(IEnumerable<ToolCallView> calls) =>
        string.Join(" ", calls.Select(Describe));

    public static string Describe(ToolCallView call) => call.Name switch
    {
        "place_part_order" => Order(call),
        "charge_customer" => Charge(call),
        _ => Other(call),
    };

    /// <summary>What happened to an approved, declined or blocked call, from its result. <see langword="null"/> while there's none.</summary>
    public static string? Outcome(ToolCallView call)
    {
        if (call.Status != ToolCallStatus.Completed || call.Result is not { } result)
        {
            return null;
        }

        if (result.StartsWith("Tool call invocation rejected.", StringComparison.Ordinal))
        {
            return result.Contains("dismissed", StringComparison.Ordinal) ? "dismissed" : "declined";
        }

        return Json(result) switch
        {
            { } json when json.TryGetProperty("error", out var error) => $"not carried out: {error.GetString()}",
            { } json when json.TryGetProperty("id", out var id) && json.TryGetProperty("total", out var total) =>
                $"placed as {id.GetString()}: {Int(json, "quantity")} x {Text(json, "sku")}, {Money(total)}",
            { } json when json.TryGetProperty("id", out var id) && json.TryGetProperty("amount", out var amount) =>
                $"charged as {id.GetString()}: {Money(amount)}",
            _ => "done",
        };
    }

    private static string Order(ToolCallView call)
    {
        var proposal = $"{Int(call.Arguments, "quantity") ?? "?"} x {Text(call.Arguments, "sku") ?? "a part"} for {Text(call.Arguments, "workOrderId") ?? "a work order"}";
        return Sentence(call, $"Ordering {proposal}");
    }

    private static string Charge(ToolCallView call)
    {
        var amount = call.Arguments is { } args && args.TryGetProperty("amount", out var value) ? Money(value) : "an amount";
        return Sentence(call, $"Charging {amount} for {Text(call.Arguments, "workOrderId") ?? "a work order"}");
    }

    private static string Sentence(ToolCallView call, string doing) => call.Status switch
    {
        ToolCallStatus.Streaming or ToolCallStatus.InProgress => $"{doing}…",
        ToolCallStatus.AwaitingApproval => $"{doing}: waiting for approval.",
        ToolCallStatus.Completed => $"{doing}: {Outcome(call)}.",
        ToolCallStatus.AwaitingClient => $"{doing}: waiting for this app.",
        _ => $"{doing}: stopped before it finished.",
    };

    private static string Other(ToolCallView call)
    {
        var (doing, done) = call.Name switch
        {
            "get_work_order" => ($"Looking up {Text(call.Arguments, "workOrderId") ?? "a work order"}", $"Looked up {Text(call.Arguments, "workOrderId") ?? "a work order"}"),
            "search_parts" => ($"Searching parts for {Text(call.Arguments, "query") ?? "a match"}", $"Searched parts for {Text(call.Arguments, "query") ?? "a match"}"),
            "get_schedule" => ("Reading today's schedule", "Read today's schedule"),
            _ => ($"Running {call.Name}", $"Ran {call.Name}"),
        };

        return call.Status switch
        {
            ToolCallStatus.Streaming or ToolCallStatus.InProgress => $"{doing}…",
            ToolCallStatus.Completed => $"{done}.",
            _ => $"{doing}: stopped before it finished.",
        };
    }

    // Arguments and results are untrusted; they're only ever shown as text.
    private static string? Text(JsonElement? json, string name) =>
        json is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Int(JsonElement? json, string name) =>
        json is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : null;

    private static string Money(JsonElement value) =>
        value.TryGetDecimal(out var amount) ? amount.ToString("C2", CultureInfo.GetCultureInfo("en-US")) : value.GetRawText();

    private static JsonElement? Json(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
