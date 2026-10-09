using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ch11.Beacon.ComponentViews;

/// <summary>
/// Turns a prefix of a JSON document (the argument text of a tool call that is still streaming) into
/// the largest valid document it implies, so a client can render what has arrived so far.
/// </summary>
/// <remarks>
/// <para>
/// The prefix is cut back to the last point where every open object and array could be closed: after
/// an opening brace or bracket, or after a complete value. A string value that is still arriving is kept
/// and closed, so a name appears as it is written. A key without its value, a dangling comma and a number
/// or literal that may still grow are dropped until they're complete.
/// </para>
/// <para>
/// The result is a preview, never the arguments: AG-UI forbids acting on arguments before
/// <c>TOOL_CALL_END</c>, and Beacon renders nothing interactive until the server has accepted them.
/// </para>
/// </remarks>
public static partial class PartialJson
{
    private enum Expect
    {
        AfterOpen,  // just after { or [ : may close
        Key,        // after a comma in an object
        Colon,      // after a key
        Value,      // after a colon, or after a comma in an array
        AfterValue, // after a value: may close
    }

    /// <summary>Completes <paramref name="prefix"/>; <see langword="null"/> when nothing renderable has arrived yet.</summary>
    public static JsonElement? TryComplete(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return null;
        }

        var text = prefix;
        List<(bool IsObject, Expect State)> stack = [];
        string? completed = null;         // the best completion found so far
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c is '{' or '[')
            {
                if (!ExpectsValue(stack))
                {
                    break;
                }

                stack.Add((c == '{', Expect.AfterOpen));
                i++;
                completed = Close(text[..i], stack);
                continue;
            }

            if (c is '}' or ']')
            {
                if (stack.Count == 0 || stack[^1].IsObject != (c == '}') || stack[^1].State is not (Expect.AfterOpen or Expect.AfterValue))
                {
                    break;
                }

                stack.RemoveAt(stack.Count - 1);
                SetValueDone(stack);
                i++;
                completed = Close(text[..i], stack);
                if (stack.Count == 0)
                {
                    break; // the document is complete
                }

                continue;
            }

            if (c == ':')
            {
                if (stack.Count == 0 || stack[^1].State != Expect.Colon)
                {
                    break;
                }

                stack[^1] = (true, Expect.Value);
                i++;
                continue;
            }

            if (c == ',')
            {
                if (stack.Count == 0 || stack[^1].State != Expect.AfterValue)
                {
                    break;
                }

                stack[^1] = (stack[^1].IsObject, stack[^1].IsObject ? Expect.Key : Expect.Value);
                i++;
                continue;
            }

            if (c == '"')
            {
                var isKey = stack.Count > 0 && stack[^1].IsObject && stack[^1].State is Expect.AfterOpen or Expect.Key;
                if (!isKey && !ExpectsValue(stack))
                {
                    break;
                }

                var end = EndOfString(text, i);
                if (end < 0)
                {
                    // Still arriving. A value can be shown as far as it goes; a key can't.
                    if (!isKey && stack.Count > 0)
                    {
                        var partial = TrimIncompleteEscape(text[i..]) + "\"";
                        SetValueDone(stack);
                        completed = Close(text[..i] + partial, stack);
                    }

                    break;
                }

                i = end + 1;
                if (isKey)
                {
                    stack[^1] = (true, Expect.Colon);
                }
                else
                {
                    SetValueDone(stack);
                    completed = Close(text[..i], stack);
                }

                continue;
            }

            // A number or a literal. It is complete only when something follows it.
            if (!ExpectsValue(stack))
            {
                break;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not (',' or '}' or ']' or ':'))
            {
                i++;
            }

            if (i == text.Length || !Scalar().IsMatch(text[start..i]))
            {
                break; // may still grow ("3" becoming "38.50"), or isn't a value at all
            }

            SetValueDone(stack);
            completed = Close(text[..i], stack);
        }

        return completed is null ? null : Parse(completed);
    }

    private static bool ExpectsValue(List<(bool IsObject, Expect State)> stack) =>
        stack.Count == 0
            ? true
            : stack[^1].IsObject ? stack[^1].State == Expect.Value : stack[^1].State is Expect.AfterOpen or Expect.Value;

    private static void SetValueDone(List<(bool IsObject, Expect State)> stack)
    {
        if (stack.Count > 0)
        {
            stack[^1] = (stack[^1].IsObject, Expect.AfterValue);
        }
    }

    // A completion is valid only where every open container may close here.
    private static string? Close(string head, List<(bool IsObject, Expect State)> stack)
    {
        if (stack.Count > 0 && stack[^1].State is not (Expect.AfterOpen or Expect.AfterValue))
        {
            return null;
        }

        var builder = new StringBuilder(head);
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            builder.Append(stack[i].IsObject ? '}' : ']');
        }

        return builder.ToString();
    }

    private static int EndOfString(string text, int openingQuote)
    {
        for (var i = openingQuote + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++; // skip the escaped character
            }
            else if (text[i] == '"')
            {
                return i;
            }
        }

        return -1;
    }

    // A string cut inside an escape sequence would close badly: drop the unfinished escape.
    private static string TrimIncompleteEscape(string unfinished)
    {
        var slash = unfinished.LastIndexOf('\\');
        if (slash < 0)
        {
            return unfinished;
        }

        var backslashes = 0;
        for (var i = slash; i >= 0 && unfinished[i] == '\\'; i--)
        {
            backslashes++;
        }

        var tail = unfinished[(slash + 1)..];
        var cut = backslashes % 2 == 1 && (tail.Length == 0 || (tail[0] == 'u' && tail.Length < 5));
        return cut ? unfinished[..slash] : unfinished;
    }

    private static JsonElement? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^(-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?|true|false|null)$", RegexOptions.CultureInvariant)]
    private static partial Regex Scalar();
}
