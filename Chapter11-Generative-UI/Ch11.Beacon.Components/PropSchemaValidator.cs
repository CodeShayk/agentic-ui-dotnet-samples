using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ch11.Beacon.Components;

/// <summary>
/// Checks a JSON value against a JSON Schema, for the keywords Beacon's prop schemas use: <c>type</c>,
/// <c>properties</c>, <c>required</c>, <c>additionalProperties</c>, <c>items</c>, <c>minItems</c>,
/// <c>maxItems</c>, <c>enum</c>, <c>minLength</c>, <c>maxLength</c>, <c>pattern</c>, <c>minimum</c>,
/// <c>maximum</c> and <c>format: date</c>.
/// </summary>
/// <remarks>
/// It is deliberately small: the catalog is Beacon's own, so its schemas only use what this class
/// checks, and a test keeps it that way. Other keywords are ignored, so if you take this into a
/// project whose schemas come from elsewhere, use a complete JSON Schema validator instead.
/// Errors name the failing location (<c>$.lanes[0].slots[1].end</c>) and never echo the value,
/// because values are model output and the errors go back to the model and into logs.
/// </remarks>
public static class PropSchemaValidator
{
    /// <summary>The keywords this validator understands. Anything else in a schema would be silently ignored.</summary>
    public static IReadOnlySet<string> SupportedKeywords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "type", "properties", "required", "additionalProperties", "items", "minItems", "maxItems", "enum",
        "minLength", "maxLength", "pattern", "minimum", "maximum", "format", "description", "title",
    };

    private const int MaxErrors = 20;
    private const int MaxDepth = 32;
    private static readonly ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.Ordinal);

    /// <summary>Validates <paramref name="value"/> against <paramref name="schema"/>.</summary>
    /// <returns>The problems found, at most 20; empty when the value conforms.</returns>
    public static IReadOnlyList<string> Validate(JsonElement value, JsonElement schema)
    {
        List<string> errors = [];
        Check(value, schema, "$", errors, depth: 0);
        return errors;
    }

    private static void Check(JsonElement value, JsonElement schema, string path, List<string> errors, int depth)
    {
        if (errors.Count >= MaxErrors || schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (depth > MaxDepth)
        {
            errors.Add($"{path}: is nested too deeply.");
            return;
        }

        if (schema.TryGetProperty("type", out var type) && !HasType(value, type))
        {
            errors.Add($"{path}: must be {Describe(type)}.");
            return; // the other keywords assume the right type
        }

        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(a => JsonElement.DeepEquals(a, value)))
        {
            errors.Add($"{path}: isn't one of the allowed values.");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                CheckObject(value, schema, path, errors, depth);
                break;
            case JsonValueKind.Array:
                CheckArray(value, schema, path, errors, depth);
                break;
            case JsonValueKind.String:
                CheckString(value.GetString()!, schema, path, errors);
                break;
            case JsonValueKind.Number:
                CheckNumber(value.GetDouble(), schema, path, errors);
                break;
        }
    }

    private static void CheckObject(JsonElement value, JsonElement schema, string path, List<string> errors, int depth)
    {
        schema.TryGetProperty("properties", out var properties);

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
            {
                if (!value.TryGetProperty(name, out _))
                {
                    errors.Add($"{path}.{name}: is required.");
                }
            }
        }

        var closed = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                Check(property.Value, propertySchema, $"{path}.{property.Name}", errors, depth + 1);
            }
            else if (closed)
            {
                // Name the location, not the property: an unexpected name is model output too.
                errors.Add($"{path}: has a property the schema doesn't allow.");
            }
        }
    }

    private static void CheckArray(JsonElement value, JsonElement schema, string path, List<string> errors, int depth)
    {
        var count = value.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var min) && count < min.GetInt32())
        {
            errors.Add($"{path}: needs at least {min.GetInt32()} item(s).");
        }

        if (schema.TryGetProperty("maxItems", out var max) && count > max.GetInt32())
        {
            errors.Add($"{path}: allows at most {max.GetInt32()} item(s).");
        }

        if (schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                Check(item, items, $"{path}[{index++}]", errors, depth + 1);
            }
        }
    }

    private static void CheckString(string text, JsonElement schema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("minLength", out var min) && text.Length < min.GetInt32())
        {
            errors.Add($"{path}: must be at least {min.GetInt32()} character(s).");
        }

        if (schema.TryGetProperty("maxLength", out var max) && text.Length > max.GetInt32())
        {
            errors.Add($"{path}: must be at most {max.GetInt32()} characters.");
        }

        if (schema.TryGetProperty("pattern", out var pattern) && !Matches(pattern.GetString()!, text))
        {
            errors.Add($"{path}: doesn't have the expected format ({pattern.GetString()}).");
        }

        if (schema.TryGetProperty("format", out var format) && format.GetString() == "date"
            && !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            errors.Add($"{path}: isn't a valid date.");
        }
    }

    private static void CheckNumber(double number, JsonElement schema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("minimum", out var min) && number < min.GetDouble())
        {
            errors.Add($"{path}: must be at least {min.GetDouble().ToString(CultureInfo.InvariantCulture)}.");
        }

        if (schema.TryGetProperty("maximum", out var max) && number > max.GetDouble())
        {
            errors.Add($"{path}: must be at most {max.GetDouble().ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static bool HasType(JsonElement value, JsonElement type) =>
        type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(t => HasType(value, t.GetString()!))
            : HasType(value, type.GetString()!);

    private static bool HasType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d) && decimal.Truncate(d) == d,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static string Describe(JsonElement type) =>
        type.ValueKind == JsonValueKind.Array
            ? string.Join(" or ", type.EnumerateArray().Select(t => Article(t.GetString()!)))
            : Article(type.GetString()!);

    private static string Article(string type) => type is "object" or "array" or "integer" ? $"an {type}" : $"a {type}";

    // Schema patterns are ECMA-262 regular expressions. The catalog's are simple enough to mean the same
    // thing in .NET; the timeout guards against a pattern that backtracks badly on hostile input.
    private static bool Matches(string pattern, string text)
    {
        try
        {
            return Patterns.GetOrAdd(pattern, p => new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))).IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
