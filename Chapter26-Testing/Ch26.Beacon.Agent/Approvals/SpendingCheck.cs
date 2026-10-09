using System.Globalization;
using System.Text.Json;
using Beacon.AI;
using Beacon.Domain;

namespace Ch26.Beacon.Agent.Approvals;

/// <summary>What the spending rule says about one tool call.</summary>
public enum SpendingVerdict
{
    /// <summary>The tool doesn't spend money.</summary>
    NotGated,

    /// <summary>Priced at or under the threshold: it runs without asking anyone.</summary>
    WithinLimit,

    /// <summary>Priced over the threshold: a dispatcher must approve it first.</summary>
    NeedsApproval,

    /// <summary>A consequential call the server can't price. It is refused, never waved through.</summary>
    Unpriceable,
}

/// <summary>
/// Prices a consequential tool call from the server's own catalog, never from a number the model or a
/// client wrote: an order is unit price times quantity, a charge is its amount. A call it can't price
/// fails closed. (llama3.2 once sent <c>"quantity":"1"</c>, a string; an earlier version of this check
/// treated "can't price" as "within the limit" and the order went through. A recording of that run is
/// now a regression test.)
/// </summary>
public static class SpendingCheck
{
    public static SpendingVerdict Assess(IFieldServiceStore store, string toolName, IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        if (toolName is not (BeaconTools.PlacePartOrderName or BeaconTools.ChargeCustomerName))
        {
            return SpendingVerdict.NotGated;
        }

        return Price(store, toolName, arguments) switch
        {
            null => SpendingVerdict.Unpriceable,
            var amount when SpendingPolicy.RequiresApproval(amount.Value) => SpendingVerdict.NeedsApproval,
            _ => SpendingVerdict.WithinLimit,
        };
    }

    public static decimal? Price(IFieldServiceStore store, string toolName, IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        var args = arguments?.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal) ?? [];
        return toolName switch
        {
            BeaconTools.PlacePartOrderName when String(args, "sku") is { } sku && store.GetPart(sku) is { } part
                && Number(args, "quantity") is { } quantity && quantity == decimal.Truncate(quantity)
                => part.UnitPrice * quantity,
            BeaconTools.ChargeCustomerName => Number(args, "amount"),
            _ => null,
        };
    }

    private static string? String(Dictionary<string, object?> args, string name) => args.GetValueOrDefault(name) switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        _ => null,
    };

    // Models send numbers as numbers or as strings ("1", "$1,795.00"); the tool's own binding accepts both,
    // so the price check must read both the same way.
    private static decimal? Number(Dictionary<string, object?> args, string name) => args.GetValueOrDefault(name) switch
    {
        decimal d => d,
        double d => (decimal)d,
        float f => (decimal)f,
        int i => i,
        long l => l,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetDecimal(out var d) => d,
        JsonElement { ValueKind: JsonValueKind.String } e => Parse(e.GetString()),
        string s => Parse(s),
        _ => null,
    };

    private static decimal? Parse(string? text) =>
        decimal.TryParse(text?.Trim().TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
}
