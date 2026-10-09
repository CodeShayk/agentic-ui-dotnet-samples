using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace Ch10.Field.Tools;

/// <summary>Contoso Climate part numbers, as typed or read from a label.</summary>
public static partial class PartNumbers
{
    /// <summary>
    /// Normalizes what was read from a label (case, stray spaces, typographic dashes) and accepts it only
    /// if the result is a well-formed part number such as <c>CAP-45-5</c>. Anything else is rejected, so
    /// free text never reaches the agent as a "part number".
    /// </summary>
    public static bool TryNormalize(string? text, [NotNullWhen(true)] out string? sku)
    {
        sku = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
        {
            return false;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            builder.Append(c is '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2212' ? '-' : char.ToUpperInvariant(c));
        }

        var candidate = builder.ToString();
        if (!Sku().IsMatch(candidate))
        {
            return false;
        }

        sku = candidate;
        return true;
    }

    [GeneratedRegex(@"^[A-Z]{3}-[A-Z0-9]{1,8}(?:-[A-Z0-9]{1,8})?$")]
    private static partial Regex Sku();
}
