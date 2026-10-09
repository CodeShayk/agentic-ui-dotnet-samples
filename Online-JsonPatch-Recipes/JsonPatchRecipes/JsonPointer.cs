using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JsonPatchRecipes;

/// <summary>
/// RFC 6901 JSON Pointers built from segments, so that a member name containing <c>/</c> or <c>~</c>
/// can't change which location a patch touches.
/// </summary>
public static partial class JsonPointer
{
    /// <summary>The last segment that appends to an array in <c>add</c>, <c>move</c> and <c>copy</c>.</summary>
    public const string End = "-";

    /// <summary>
    /// Escapes one reference token: <c>~</c> becomes <c>~0</c>, then <c>/</c> becomes <c>~1</c>. Escaping
    /// <c>/</c> first would escape the tilde of every <c>~1</c> it had just written.
    /// </summary>
    public static string Escape(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>
    /// Decodes one reference token in the opposite order: <c>~1</c> first, then <c>~0</c>, so that
    /// <c>~01</c> decodes to the two characters <c>~1</c> and not to <c>/</c>.
    /// </summary>
    public static string Unescape(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    /// <summary>The pointer to a location. No segments is the empty pointer: the whole document.</summary>
    public static string Create(params ReadOnlySpan<string> segments)
    {
        var pointer = new StringBuilder();
        foreach (var segment in segments)
        {
            pointer.Append('/').Append(Escape(segment));
        }

        return pointer.ToString();
    }

    /// <summary>A member of the object at <paramref name="pointer"/>.</summary>
    public static string Append(string pointer, string member) => $"{pointer}/{Escape(member)}";

    /// <summary>An element of the array at <paramref name="pointer"/>.</summary>
    public static string Append(string pointer, int index) =>
        $"{pointer}/{index.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>True when <paramref name="pointer"/> is a JSON Pointer by the AG-UI 1.0 schema's own pattern.</summary>
    public static bool IsValid(string pointer) => Pattern().IsMatch(pointer);

    // The schema's pattern is ^(/([^/~]|~[01])*)*$; in .NET, $ also matches before a final line break, so \z.
    [GeneratedRegex(@"^(/([^/~]|~[01])*)*\z")]
    private static partial Regex Pattern();
}
