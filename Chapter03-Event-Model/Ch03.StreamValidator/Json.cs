using System.Text.Json;

namespace Ch03.StreamValidator;

/// <summary>Small helpers for reading wire JSON without trusting its shape.</summary>
internal static class Json
{
    /// <summary>The largest integer a JSON number can carry exactly (2^53 - 1); the schema bounds counts and timestamps by it.</summary>
    public const long MaxSafeInteger = 9_007_199_254_740_991;

    /// <summary>The string value of a property, or null when it is absent or not a string.</summary>
    public static string? String(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The boolean value of a property, or null when it is absent or not a boolean.</summary>
    public static bool? Boolean(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    /// <summary>Whether a number is an integer within the JSON safe-integer range.</summary>
    public static bool IsSafeInteger(JsonElement value, out long result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
        {
            return false;
        }

        if (decimal.Truncate(number) != number || number > MaxSafeInteger || number < -MaxSafeInteger)
        {
            return false;
        }

        result = (long)number;
        return true;
    }

    /// <summary>Whether a string is an RFC 6901 JSON Pointer: empty, or segments that each start with '/' and escape '~' as ~0 or ~1.</summary>
    public static bool IsJsonPointer(string pointer)
    {
        if (pointer.Length == 0)
        {
            return true;
        }

        if (pointer[0] != '/')
        {
            return false;
        }

        for (var i = 0; i < pointer.Length; i++)
        {
            if (pointer[i] == '~' && (i + 1 >= pointer.Length || pointer[i + 1] is not ('0' or '1')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Appends a property name or array index to a JSON Pointer, escaping as RFC 6901 requires.</summary>
    public static string Append(string pointer, string segment) =>
        $"{pointer}/{segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";

    public static string Append(string pointer, int index) => $"{pointer}/{index}";

    /// <summary>Describes a JSON value's kind for a diagnostic message.</summary>
    public static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "nothing",
    };

    /// <summary>
    /// Adds the ids a later REASONING_ENCRYPTED_VALUE may name: a message's own id (unless it is an
    /// activity message, which carries no encrypted value) and the ids of its tool calls.
    /// </summary>
    public static void CollectEntityIds(JsonElement message, ISet<string> ids)
    {
        if (message.ValueKind != JsonValueKind.Object || String(message, "role") == "activity")
        {
            return;
        }

        if (String(message, "id") is { } id)
        {
            ids.Add(id);
        }

        if (message.TryGetProperty("toolCalls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                if (String(call, "id") is { } callId)
                {
                    ids.Add(callId);
                }
            }
        }
    }
}
