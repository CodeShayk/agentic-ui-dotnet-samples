// Copied from Chapter 14 (Ch14.Beacon.Agent/Approvals/ToolArguments.cs); unchanged apart from the namespace.
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ch27.Beacon.Agent.Security.Approvals;

/// <summary>
/// A tool call's arguments in the one form the server records, compares and prices: a JSON object.
/// They arrive as CLR values from the model, as JSON from a client and as <see cref="AIFunctionArguments"/>
/// at the tool; converting all three the same way is what makes "exactly this call" checkable.
/// </summary>
public static class ToolArguments
{
    // Microsoft.Extensions.AI's defaults write indented JSON; records and logs want one line.
    private static readonly JsonSerializerOptions Compact = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    public static JsonElement ToJson(IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        IDictionary<string, object?> values = arguments switch
        {
            null => new Dictionary<string, object?>(),
            IDictionary<string, object?> dictionary => dictionary,
            _ => arguments.ToDictionary(StringComparer.Ordinal),
        };

        return JsonSerializer.SerializeToElement(values, Compact.GetTypeInfo(typeof(IDictionary<string, object?>)));
    }

    /// <summary>The dictionary a <see cref="FunctionCallContent"/> carries.</summary>
    public static Dictionary<string, object?> ToDictionary(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal)
            : [];

    /// <summary>Whether two argument objects are the same call: same properties, same values (numbers compared by value).</summary>
    public static bool Same(JsonElement left, JsonElement right) => JsonElement.DeepEquals(left, right);

    public static string? String(JsonElement arguments, string name) =>
        Property(arguments, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static int? Integer(JsonElement arguments, string name) =>
        Property(arguments, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    public static decimal? Decimal(JsonElement arguments, string name) =>
        Property(arguments, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDecimal(out var number) ? number : null;

    private static JsonElement? Property(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) ? value : null;
}
