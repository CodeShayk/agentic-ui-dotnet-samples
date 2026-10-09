using System.Text;
using System.Text.Json;

namespace Ch09.Beacon.ToolCards;

/// <summary>
/// Reads what it can from a tool call's argument text while it is still arriving, for a preview.
/// </summary>
/// <remarks>
/// The concatenated <c>TOOL_CALL_ARGS</c> deltas are a prefix of a JSON document, such as
/// <c>{"workOrderId":"WO-10</c>. AG-UI says a consumer must not act on them before <c>TOOL_CALL_END</c>;
/// showing them is not acting. This returns the top-level string, number and Boolean values read so
/// far, including a string value that is still arriving, and never throws: text that isn't JSON yields
/// what was read before the problem.
/// </remarks>
public static class PartialArguments
{
    public static IReadOnlyDictionary<string, string> Read(string? text)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return values;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
        string? property = null;

        try
        {
            while (reader.Read())
            {
                if (reader.CurrentDepth != 1)
                {
                    continue; // only the top-level object's own values
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        property = reader.GetString();
                        break;
                    case JsonTokenType.String when property is not null:
                        values[property] = reader.GetString() ?? string.Empty;
                        property = null;
                        break;
                    case JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False when property is not null:
                        values[property] = Encoding.UTF8.GetString(reader.ValueSpan);
                        property = null;
                        break;
                    default:
                        property = null; // null, an object or an array: nothing to preview
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return values;
        }

        // A string value still arriving: the reader stopped at its opening quote.
        if (property is not null)
        {
            var rest = Encoding.UTF8.GetString(bytes, (int)reader.BytesConsumed, bytes.Length - (int)reader.BytesConsumed).TrimStart();
            rest = rest.StartsWith(':') ? rest[1..].TrimStart() : rest;
            if (rest.StartsWith('"'))
            {
                values[property] = Unescape(rest[1..]);
            }
        }

        return values;
    }

    // Decodes the escapes of an unfinished JSON string, dropping one that is cut off.
    private static string Unescape(string partial)
    {
        var text = new StringBuilder(partial.Length);
        for (var i = 0; i < partial.Length; i++)
        {
            if (partial[i] != '\\')
            {
                text.Append(partial[i]);
                continue;
            }

            if (i + 1 >= partial.Length)
            {
                break;
            }

            var escape = partial[++i];
            switch (escape)
            {
                case 'n': text.Append('\n'); break;
                case 't': text.Append('\t'); break;
                case 'r': text.Append('\r'); break;
                case 'b' or 'f': break;
                case 'u' when i + 4 < partial.Length && int.TryParse(partial.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code):
                    text.Append((char)code);
                    i += 4;
                    break;
                case 'u':
                    return text.ToString();
                default:
                    text.Append(escape); // \" \\ \/
                    break;
            }
        }

        return text.ToString();
    }
}
