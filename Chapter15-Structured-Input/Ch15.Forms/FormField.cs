using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ch15.Forms;

/// <summary>How a field is entered and what JSON value it produces.</summary>
public enum FieldKind
{
    /// <summary>A JSON string typed by the person (<c>type: "string"</c>).</summary>
    Text,

    /// <summary>One string from a fixed list (<c>enum</c>).</summary>
    Choice,

    /// <summary>A JSON number (<c>type: "number"</c>).</summary>
    Number,

    /// <summary>A JSON number with no fractional part (<c>type: "integer"</c>).</summary>
    Integer,

    /// <summary><c>true</c> or <c>false</c> (<c>type: "boolean"</c>).</summary>
    Boolean,
}

/// <summary>One property of a <see cref="FormSchema"/>, with the constraints the subset supports.</summary>
/// <param name="Name">The property name in the payload.</param>
/// <param name="Kind">How the value is entered.</param>
/// <param name="Label">What the person sees: the property's <c>title</c>, or its name made readable.</param>
public sealed record FormField(string Name, FieldKind Kind, string Label)
{
    /// <summary>The property's <c>description</c>, shown as a hint under the label.</summary>
    public string? Description { get; init; }

    /// <summary>Listed in the schema's <c>required</c>.</summary>
    public bool Required { get; init; }

    /// <summary>The <c>pattern</c> a text value must match somewhere (JSON Schema patterns aren't anchored).</summary>
    public string? Pattern { get; init; }

    /// <summary>The smallest number of characters (Unicode code points) a text value may have.</summary>
    public int? MinLength { get; init; }

    /// <summary>The largest number of characters (Unicode code points) a text value may have.</summary>
    public int? MaxLength { get; init; }

    /// <summary>The allowed values of a <see cref="FieldKind.Choice"/> field, in schema order.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>The inclusive lower bound of a number.</summary>
    public decimal? Minimum { get; init; }

    /// <summary>The inclusive upper bound of a number.</summary>
    public decimal? Maximum { get; init; }

    /// <summary>The exclusive lower bound of a number.</summary>
    public decimal? ExclusiveMinimum { get; init; }

    /// <summary>The exclusive upper bound of a number.</summary>
    public decimal? ExclusiveMaximum { get; init; }

    /// <summary>The property's <c>default</c>, if any. A form starts from it.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>The compiled <see cref="Pattern"/>, with a match timeout.</summary>
    internal Regex? PatternRegex { get; init; }
}

/// <summary>A problem with one field of an answer, or with the answer as a whole when <see cref="Field"/> is empty.</summary>
/// <param name="Field">The property name, or <see cref="string.Empty"/> for the whole answer.</param>
/// <param name="Message">A sentence a person can act on.</param>
public sealed record FieldError(string Field, string Message);
