using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ch13.Beacon.Agent;

/// <summary>
/// Reads the JSON a model has streamed so far. A tool call's arguments arrive in fragments, and a
/// prefix such as <c>{"summary":"The unit short-cyc</c> isn't JSON yet. <see cref="Parse"/> completes it
/// with the fewest characters that make it valid (it closes an open string value, then every open
/// object and array) and drops what can't be completed honestly: a key with no value yet, a
/// half-written number or literal, half an escape sequence.
/// </summary>
public static class PartialJson
{
    /// <summary>Parses the longest valid reading of <paramref name="prefix"/>.</summary>
    /// <param name="prefix">The text received so far.</param>
    /// <param name="openProperty">
    /// The name of the top-level property whose string value is still arriving, if any. A consumer
    /// can show a growing sentence, but shouldn't act on a half-written identifier.
    /// </param>
    /// <returns>The value so far, or <see langword="null"/> when nothing usable has arrived.</returns>
    public static JsonNode? Parse(string prefix, out string? openProperty)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        openProperty = null;

        List<char> containers = [];   // '{' or '[' for each container still open
        List<bool> expectsKey = [];   // per container: is the next string a key?
        List<string?> keys = [];      // per container: the key whose value is being read
        var safeLength = 0;           // prefix[..safeLength] + safeClosers is valid JSON
        var safeClosers = string.Empty;

        var inString = false;
        var stringIsKey = false;
        var stringStart = 0;
        var escapeAt = -1;            // the backslash of an escape still in progress
        var unicodeDigits = 0;

        void MarkSafe(int length)
        {
            safeLength = length;
            safeClosers = Closers(containers);
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            var c = prefix[i];
            if (inString)
            {
                if (unicodeDigits > 0)
                {
                    escapeAt = --unicodeDigits == 0 ? -1 : escapeAt;
                }
                else if (escapeAt >= 0)
                {
                    (unicodeDigits, escapeAt) = c == 'u' ? (4, escapeAt) : (0, -1);
                }
                else if (c == '\\')
                {
                    escapeAt = i;
                }
                else if (c == '"')
                {
                    inString = false;
                    if (stringIsKey)
                    {
                        keys[^1] = DecodeString(prefix, stringStart, i);
                    }
                    else
                    {
                        MarkSafe(i + 1);
                    }
                }

                continue;
            }

            switch (c)
            {
                case '{' or '[':
                    containers.Add(c);
                    expectsKey.Add(c == '{');
                    keys.Add(null);
                    MarkSafe(i + 1);
                    break;

                case '}' or ']':
                    if (containers.Count == 0)
                    {
                        return null; // not JSON at all
                    }

                    containers.RemoveAt(containers.Count - 1);
                    expectsKey.RemoveAt(expectsKey.Count - 1);
                    keys.RemoveAt(keys.Count - 1);
                    MarkSafe(i + 1);
                    break;

                case '"':
                    inString = true;
                    stringStart = i;
                    stringIsKey = containers.Count > 0 && containers[^1] == '{' && expectsKey[^1];
                    break;

                case ':':
                    if (containers.Count > 0)
                    {
                        expectsKey[^1] = false;
                    }

                    break;

                case ',':
                    if (containers.Count > 0 && containers[^1] == '{')
                    {
                        expectsKey[^1] = true;
                    }

                    break;

                case ' ' or '\t' or '\r' or '\n':
                    break;

                default:
                    // A number or a literal is complete only once something follows it: "12" may become "125".
                    var end = i;
                    while (end < prefix.Length && !IsDelimiter(prefix[end]))
                    {
                        end++;
                    }

                    if (end < prefix.Length)
                    {
                        MarkSafe(end);
                    }

                    i = end - 1;
                    break;
            }
        }

        string candidate;
        if (inString && !stringIsKey)
        {
            // A string value still arriving: keep what's there, minus any half-written escape.
            var length = escapeAt >= 0 ? escapeAt : prefix.Length;
            candidate = string.Concat(prefix.AsSpan(0, length), "\"", Closers(containers));
            if (containers is ['{'])
            {
                openProperty = keys[0];
            }
        }
        else if (safeLength > 0)
        {
            candidate = string.Concat(prefix.AsSpan(0, safeLength), safeClosers);
        }
        else
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(candidate);
        }
        catch (JsonException)
        {
            openProperty = null;
            return null;
        }
    }

    private static bool IsDelimiter(char c) => c is ',' or '}' or ']' or ' ' or '\t' or '\r' or '\n';

    private static string Closers(List<char> containers)
    {
        var closers = new StringBuilder(containers.Count);
        for (var i = containers.Count - 1; i >= 0; i--)
        {
            closers.Append(containers[i] == '{' ? '}' : ']');
        }

        return closers.ToString();
    }

    private static string? DecodeString(string text, int openingQuote, int closingQuote)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text[openingQuote..(closingQuote + 1)]));
        return reader.Read() ? reader.GetString() : null;
    }
}
