using Ch15.Forms;
using Microsoft.AspNetCore.Components;

namespace Ch15.Beacon.Desk.Components.Questions;

public sealed partial class SchemaForm
{
    private readonly Dictionary<string, ElementReference> _inputs = new(StringComparer.Ordinal);

    /// <summary>The form's state. The component writes what Dana types into it.</summary>
    [Parameter, EditorRequired] public FormDraft Draft { get; set; } = default!;

    /// <summary>Makes element IDs unique when several forms share a page (one per open interrupt).</summary>
    [Parameter, EditorRequired] public string IdPrefix { get; set; } = default!;

    [Parameter] public bool Disabled { get; set; }

    /// <summary>Moves focus to the first field with a problem. Returns false if there is none.</summary>
    public async Task<bool> FocusFirstInvalidAsync()
    {
        var field = Draft.Schema.Fields.FirstOrDefault(f => Draft.Check(f.Name).Count > 0);
        if (field is null || !_inputs.TryGetValue(field.Name, out var input))
        {
            return false;
        }

        await input.FocusAsync();
        return true;
    }

    private string FieldId(FormField field) => $"{IdPrefix}-{field.Name}";

    // Shown once the field was visited or a submit was attempted; recomputed from the draft on every render.
    private string? ErrorFor(FormField field) =>
        Draft.IsTouched(field.Name) && Draft.Check(field.Name) is [var first, ..] ? first.Message : null;

    private string? DescribedBy(FormField field, string? error)
    {
        List<string> ids = [];
        if (field.Description is not null)
        {
            ids.Add($"{FieldId(field)}-hint");
        }

        if (error is not null)
        {
            ids.Add($"{FieldId(field)}-error");
        }

        return ids.Count > 0 ? string.Join(' ', ids) : null;
    }

    private void Choose(FormField field, string option)
    {
        Draft.SetText(field.Name, option);
        Draft.Touch(field.Name);
    }

    // A radio group is focused through its first option, which is filed under the field's name.
    private static string RadioKey(FormField field, int index) => index == 0 ? field.Name : $"{field.Name}#{index}";

    private RenderFragment Optional(FormField field) => builder =>
    {
        if (!field.Required && field.Kind != FieldKind.Boolean)
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "ch15-optional");
            builder.AddContent(2, "(optional)");
            builder.CloseElement();
        }
    };

    private RenderFragment Hint(FormField field) => builder =>
    {
        if (field.Description is { } hint)
        {
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "id", $"{FieldId(field)}-hint");
            builder.AddAttribute(2, "class", "ch15-hint");
            builder.AddContent(3, hint);
            builder.CloseElement();
        }
    };

    private RenderFragment Error(FormField field, string? error) => builder =>
    {
        if (error is not null)
        {
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "id", $"{FieldId(field)}-error");
            builder.AddAttribute(2, "class", "ch15-error");
            builder.AddContent(3, error);
            builder.CloseElement();
        }
    };

    // Enum values are data ("nameplate"); show them as words a person reads ("Nameplate").
    private static string Display(string option) =>
        option.Length == 0 ? option : char.ToUpperInvariant(option[0]) + option[1..];
}
