using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Ch15.Forms;

/// <summary>
/// What a person has entered into a generated form so far. Renderers bind their controls to it
/// (text for text, choice and number fields, a flag for booleans), ask it for each field's errors
/// and, when nothing is wrong, take the payload from it. It never talks to an agent.
/// </summary>
/// <remarks>
/// Values are kept as the person typed them, so a half-typed number stays on screen while it is
/// wrong. Each check converts the input to the JSON value it would send and runs
/// <see cref="FormSchema.ValidateValue"/>, the same check the agent runs on the payload.
/// </remarks>
public sealed class FormDraft
{
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _flags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);

    /// <summary>Starts a draft from the schema's defaults.</summary>
    public FormDraft(FormSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = schema;

        foreach (var field in schema.Fields)
        {
            if (field.Kind == FieldKind.Boolean)
            {
                _flags[field.Name] = field.Default is { ValueKind: JsonValueKind.True };
            }
            else
            {
                _text[field.Name] = field.Default switch
                {
                    { ValueKind: JsonValueKind.String } text => text.GetString()!,
                    { ValueKind: JsonValueKind.Number } number => number.GetRawText(),
                    _ => string.Empty,
                };
            }
        }
    }

    /// <summary>The form this draft fills in.</summary>
    public FormSchema Schema { get; }

    /// <summary>The text entered for a text, choice or number field.</summary>
    public string Text(string field) => _text.GetValueOrDefault(field, string.Empty);

    /// <summary>Replaces the text entered for a text, choice or number field.</summary>
    public void SetText(string field, string? value) => _text[Known(field).Name] = value ?? string.Empty;

    /// <summary>Whether a boolean field is ticked.</summary>
    public bool Flag(string field) => _flags.GetValueOrDefault(field);

    /// <summary>Ticks or clears a boolean field.</summary>
    public void SetFlag(string field, bool value) => _flags[Known(field).Name] = value;

    /// <summary>Marks a field as visited, so its errors may be shown (renderers call this when the field loses focus).</summary>
    public void Touch(string field) => _touched.Add(Known(field).Name);

    /// <summary>Marks every field as visited, as a submit attempt does.</summary>
    public void TouchAll() => _touched.UnionWith(Schema.Fields.Select(f => f.Name));

    /// <summary>Whether the field was visited.</summary>
    public bool IsTouched(string field) => _touched.Contains(field);

    /// <summary>The problems with one field's current input.</summary>
    public IReadOnlyList<FieldError> Check(string field)
    {
        var definition = Known(field);
        return TryConvert(definition, out var value, out var problem)
            ? FormSchema.ValidateValue(definition, value)
            : [problem!];
    }

    /// <summary>The problems with every field, in form order.</summary>
    public IReadOnlyList<FieldError> CheckAll() => [.. Schema.Fields.SelectMany(f => Check(f.Name))];

    /// <summary>Builds the answer, if the input is valid.</summary>
    /// <param name="payload">The JSON object to send as the resume payload.</param>
    /// <param name="errors">What is wrong otherwise.</param>
    public bool TryGetPayload(out JsonElement payload, out IReadOnlyList<FieldError> errors)
    {
        errors = CheckAll();
        payload = default;
        if (errors.Count > 0)
        {
            return false;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in Schema.Fields)
            {
                if (TryConvert(field, out var value, out _) && value is { } present)
                {
                    writer.WritePropertyName(field.Name);
                    present.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        payload = document.RootElement.Clone();
        return true;
    }

    // The JSON value the input stands for: absent when left empty, a string, a number or a boolean.
    private bool TryConvert(FormField field, out JsonElement? value, out FieldError? problem)
    {
        value = null;
        problem = null;

        if (field.Kind == FieldKind.Boolean)
        {
            value = Json(writer => writer.WriteBooleanValue(Flag(field.Name)));
            return true;
        }

        var text = Text(field.Name).Trim();
        if (text.Length == 0)
        {
            return true;
        }

        switch (field.Kind)
        {
            case FieldKind.Number or FieldKind.Integer:
                if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    problem = new FieldError(field.Name, $"{field.Label} must be a number.");
                    return false;
                }

                value = Json(writer => writer.WriteNumberValue(number));
                return true;

            default:
                value = Json(writer => writer.WriteStringValue(text));
                return true;
        }
    }

    private FormField Known(string field) =>
        Schema.Field(field) ?? throw new ArgumentException($"The form has no field named '{field}'.", nameof(field));

    private static JsonElement Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
