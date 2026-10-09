using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ch15.Forms;

/// <summary>
/// A form described by a JSON Schema. Beacon supports a deliberately small subset: an object whose
/// properties are strings (with <c>pattern</c>, <c>minLength</c>, <c>maxLength</c> or <c>enum</c>),
/// numbers or integers (with <c>minimum</c>, <c>maximum</c> and their exclusive forms) or booleans,
/// plus <c>required</c>, <c>additionalProperties</c>, <c>title</c>, <c>description</c> and <c>default</c>.
/// <c>format</c> is accepted as an annotation and not checked, as JSON Schema 2020-12 does by default.
/// </summary>
/// <remarks>
/// Any other keyword makes the whole schema unsupported. A client that can't render every constraint
/// shouldn't render the form at all: it would collect an answer the agent rejects, or worse, one it
/// accepts for the wrong reason. The client falls back to the interrupt's message instead.
/// </remarks>
public sealed class FormSchema
{
    /// <summary>The most fields a form may have.</summary>
    public const int MaxFields = 20;

    /// <summary>The longest <c>pattern</c> accepted.</summary>
    public const int MaxPatternLength = 200;

    /// <summary>How long one pattern match may run before the value is rejected.</summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly HashSet<string> RootKeywords = new(StringComparer.Ordinal)
        { "$schema", "type", "title", "description", "properties", "required", "additionalProperties" };

    private static readonly HashSet<string> CommonKeywords = new(StringComparer.Ordinal) { "type", "title", "description", "default" };
    private static readonly HashSet<string> TextKeywords = new(StringComparer.Ordinal) { "minLength", "maxLength", "pattern", "enum", "format" };
    private static readonly HashSet<string> NumberKeywords = new(StringComparer.Ordinal) { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum" };

    private readonly Dictionary<string, FormField> _byName;

    private FormSchema(string? title, string? description, IReadOnlyList<FormField> fields, bool allowsOtherProperties)
    {
        Title = title;
        Description = description;
        Fields = fields;
        AllowsOtherProperties = allowsOtherProperties;
        _byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    /// <summary>The schema's <c>title</c>: the form's heading.</summary>
    public string? Title { get; }

    /// <summary>The schema's <c>description</c>.</summary>
    public string? Description { get; }

    /// <summary>The fields, in the order the schema lists its properties.</summary>
    public IReadOnlyList<FormField> Fields { get; }

    /// <summary>False when the schema says <c>additionalProperties: false</c>.</summary>
    public bool AllowsOtherProperties { get; }

    /// <summary>The field with this property name, if there is one.</summary>
    public FormField? Field(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Reads a schema, or explains why it is outside the supported subset.</summary>
    /// <param name="schema">The <c>responseSchema</c>.</param>
    /// <param name="form">The form, when the schema is supported.</param>
    /// <param name="problems">What isn't supported, with JSON Pointer locations; empty on success.</param>
    public static bool TryParse(JsonElement schema, [NotNullWhen(true)] out FormSchema? form, out IReadOnlyList<string> problems)
    {
        List<string> found = [];
        form = null;

        if (schema.ValueKind != JsonValueKind.Object)
        {
            problems = ["The schema must be a JSON object."];
            return false;
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            if (!RootKeywords.Contains(keyword.Name))
            {
                found.Add($"Unsupported keyword '{keyword.Name}' at the root.");
            }
        }

        if (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object")
        {
            found.Add("The root must have \"type\": \"object\".");
        }

        var allowsOther = true;
        if (schema.TryGetProperty("additionalProperties", out var additional))
        {
            if (additional.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                allowsOther = additional.GetBoolean();
            }
            else
            {
                found.Add("additionalProperties must be true or false.");
            }
        }

        HashSet<string> required = new(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredList))
        {
            if (requiredList.ValueKind != JsonValueKind.Array || requiredList.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String))
            {
                found.Add("required must be an array of property names.");
            }
            else
            {
                required.UnionWith(requiredList.EnumerateArray().Select(r => r.GetString()!));
            }
        }

        List<FormField> fields = [];
        if (schema.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
            {
                found.Add("properties must be an object.");
            }
            else
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (ParseField(property.Name, property.Value, required.Contains(property.Name), found) is { } field)
                    {
                        fields.Add(field);
                    }
                }
            }
        }

        foreach (var name in required.Where(r => !fields.Any(f => f.Name == r) && !found.Any(p => p.Contains($"/properties/{r}", StringComparison.Ordinal))))
        {
            found.Add($"required names '{name}', which isn't a property.");
        }

        if (fields.Count > MaxFields)
        {
            found.Add($"A form may have at most {MaxFields} fields.");
        }

        problems = found;
        if (found.Count > 0)
        {
            return false;
        }

        form = new FormSchema(String(schema, "title"), String(schema, "description"), fields, allowsOther);
        return true;
    }

    /// <summary>Checks a whole answer against the form: required fields, every value and, if the schema closes it, no other properties.</summary>
    /// <param name="payload">The answer, as it arrived.</param>
    /// <returns>Every problem found; empty when the answer is valid.</returns>
    public IReadOnlyList<FieldError> Validate(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return [new FieldError(string.Empty, "The answer must be a JSON object.")];
        }

        List<FieldError> errors = [];
        foreach (var field in Fields)
        {
            errors.AddRange(ValidateValue(field, payload.TryGetProperty(field.Name, out var value) ? value : null));
        }

        if (!AllowsOtherProperties)
        {
            foreach (var property in payload.EnumerateObject().Where(p => !_byName.ContainsKey(p.Name)))
            {
                errors.Add(new FieldError(property.Name, $"'{property.Name}' isn't a field of this form."));
            }
        }

        return errors;
    }

    /// <summary>Checks one field's value. <see langword="null"/> means the property is absent.</summary>
    public static IReadOnlyList<FieldError> ValidateValue(FormField field, JsonElement? value)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (value is not { } present)
        {
            return field.Required ? [Error(field, $"{field.Label} is required.")] : [];
        }

        return field.Kind switch
        {
            FieldKind.Text => ValidateText(field, present),
            FieldKind.Choice => present.ValueKind == JsonValueKind.String && field.Options.Contains(present.GetString()!, StringComparer.Ordinal)
                ? []
                : [Error(field, $"{field.Label} must be one of: {string.Join(", ", field.Options)}.")],
            FieldKind.Number or FieldKind.Integer => ValidateNumber(field, present),
            FieldKind.Boolean => present.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? []
                : [Error(field, $"{field.Label} must be true or false.")],
            _ => [Error(field, $"{field.Label} can't be checked.")],
        };
    }

    private static List<FieldError> ValidateText(FormField field, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return [Error(field, $"{field.Label} must be text.")];
        }

        var text = value.GetString()!;
        var length = text.EnumerateRunes().Count(); // JSON Schema counts code points, not UTF-16 units

        // Lengths first: they are cheap, and they bound the input the pattern has to scan.
        if (field.MinLength is { } min && length < min)
        {
            return [Error(field, $"{field.Label} must be at least {min} characters.")];
        }

        if (field.MaxLength is { } max && length > max)
        {
            return [Error(field, $"{field.Label} must be at most {max} characters.")];
        }

        if (field.PatternRegex is { } pattern)
        {
            try
            {
                if (!pattern.IsMatch(text))
                {
                    return [Error(field, $"{field.Label} isn't in the expected format.")];
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return [Error(field, $"{field.Label} couldn't be checked.")];
            }
        }

        return [];
    }

    private static List<FieldError> ValidateNumber(FormField field, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
        {
            return [Error(field, $"{field.Label} must be a number.")];
        }

        if (field.Kind == FieldKind.Integer && decimal.Truncate(number) != number)
        {
            return [Error(field, $"{field.Label} must be a whole number.")];
        }

        string? problem =
            field.Minimum is { } min && number < min ? $"at least {Format(min)}"
            : field.Maximum is { } max && number > max ? $"at most {Format(max)}"
            : field.ExclusiveMinimum is { } xmin && number <= xmin ? $"more than {Format(xmin)}"
            : field.ExclusiveMaximum is { } xmax && number >= xmax ? $"less than {Format(xmax)}"
            : null;

        return problem is null ? [] : [Error(field, $"{field.Label} must be {problem}.")];
    }

    private static FormField? ParseField(string name, JsonElement property, bool required, List<string> problems)
    {
        var at = $"/properties/{name}";
        if (property.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{at} must be an object.");
            return null;
        }

        var hasEnum = property.TryGetProperty("enum", out var enumValues);
        var typeName = property.TryGetProperty("type", out var type) ? type : (JsonElement?)null;
        if (typeName is { ValueKind: not JsonValueKind.String })
        {
            problems.Add($"{at}/type must be a single type name.");
            return null;
        }

        FieldKind? kind = typeName?.GetString() switch
        {
            null when hasEnum => FieldKind.Choice,
            "string" => hasEnum ? FieldKind.Choice : FieldKind.Text,
            "number" => FieldKind.Number,
            "integer" => FieldKind.Integer,
            "boolean" => FieldKind.Boolean,
            _ => null,
        };

        if (kind is null)
        {
            problems.Add($"{at} has a type Beacon's forms don't support ({typeName?.GetString() ?? "none"}).");
            return null;
        }

        var allowed = kind switch
        {
            FieldKind.Text or FieldKind.Choice => TextKeywords,
            FieldKind.Number or FieldKind.Integer => NumberKeywords,
            _ => [],
        };

        var before = problems.Count;
        foreach (var keyword in property.EnumerateObject().Where(k => !CommonKeywords.Contains(k.Name) && !allowed.Contains(k.Name)))
        {
            problems.Add($"Unsupported keyword '{keyword.Name}' at {at}.");
        }

        var field = new FormField(name, kind.Value, String(property, "title") ?? Humanize(name))
        {
            Description = String(property, "description"),
            Required = required,
            Default = property.TryGetProperty("default", out var defaultValue) ? defaultValue.Clone() : null,
            MinLength = Count(property, "minLength", at, problems),
            MaxLength = Count(property, "maxLength", at, problems),
            Minimum = Number(property, "minimum", at, problems),
            Maximum = Number(property, "maximum", at, problems),
            ExclusiveMinimum = Number(property, "exclusiveMinimum", at, problems),
            ExclusiveMaximum = Number(property, "exclusiveMaximum", at, problems),
        };

        if (kind == FieldKind.Choice)
        {
            if (enumValues.ValueKind != JsonValueKind.Array || enumValues.GetArrayLength() == 0
                || enumValues.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            {
                problems.Add($"{at}/enum must be a non-empty array of strings.");
            }
            else
            {
                field = field with { Options = [.. enumValues.EnumerateArray().Select(v => v.GetString()!)] };
            }
        }

        if (property.TryGetProperty("pattern", out var pattern))
        {
            if (pattern.ValueKind != JsonValueKind.String || pattern.GetString()!.Length > MaxPatternLength)
            {
                problems.Add($"{at}/pattern must be a string of at most {MaxPatternLength} characters.");
            }
            else
            {
                try
                {
                    // ECMAScript semantics, as JSON Schema specifies: \d and \w mean ASCII only, and $ means the end.
                    field = field with
                    {
                        Pattern = pattern.GetString(),
                        PatternRegex = new Regex(EndAnchored(pattern.GetString()!), RegexOptions.ECMAScript, PatternTimeout),
                    };
                }
                catch (ArgumentException)
                {
                    problems.Add($"{at}/pattern isn't a regular expression Beacon can run.");
                }
            }
        }

        return problems.Count == before ? field : null;
    }

    // ECMA-262's $ matches only at the end of the input. .NET's also matches before a final newline, even in
    // ECMAScript mode, so ^HP24S-[0-9]{4}-[0-9]{4}$ would accept "HP24S-2207-1184\n". Each $ anchor (not one
    // escaped, not one inside a character class) becomes $(?!\n), which matches only at the very end.
    internal static string EndAnchored(string pattern)
    {
        var anchored = new StringBuilder(pattern.Length + 8);
        var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            anchored.Append(c);
            if (c == '\\' && i + 1 < pattern.Length)
            {
                anchored.Append(pattern[++i]); // an escape: \$ is a literal dollar sign
            }
            else if (c == '[')
            {
                inClass = true;
            }
            else if (c == ']')
            {
                inClass = false;
            }
            else if (c == '$' && !inClass)
            {
                anchored.Append(@"(?!\n)");
            }
        }

        return anchored.ToString();
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Count(JsonElement property, string name, string at, List<string> problems)
    {
        if (!property.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0)
        {
            return count;
        }

        problems.Add($"{at}/{name} must be a non-negative integer.");
        return null;
    }

    private static decimal? Number(JsonElement property, string name, string at, List<string> problems)
    {
        if (!property.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }

        problems.Add($"{at}/{name} must be a number.");
        return null;
    }

    // "serialNumber" -> "Serial number", for properties without a title.
    private static string Humanize(string name)
    {
        var text = new StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (text.Length == 0)
            {
                text.Append(char.ToUpperInvariant(c));
            }
            else if (char.IsUpper(c))
            {
                text.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c is '_' or '-' ? ' ' : c);
            }
        }

        return text.ToString();
    }

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static FieldError Error(FormField field, string message) => new(field.Name, message);
}
