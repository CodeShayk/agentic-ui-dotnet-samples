using Ch15.Forms;
using Microsoft.Maui.Controls.Shapes;

namespace Ch15.Beacon.Field;

/// <summary>
/// One question as a native form: a control per field of its response schema, each with a description
/// screen readers announce, its hint and an error line that appears once the field is left. The rules
/// are Ch15.Forms', the same ones the Desk and the agent run; this view only renders them.
/// </summary>
public sealed class QuestionFormView : ContentView
{
    private readonly Dictionary<string, (VisualElement Focus, Label Error)> _fields = new(StringComparer.Ordinal);
    private readonly VerticalStackLayout _form = new() { Spacing = 10 };
    private readonly Label _notice = new() { IsVisible = false };
    private readonly Button _skip = new() { Text = "I can't answer this" };

    public QuestionFormView(FormQuestion question)
    {
        Question = question;

        var title = new Label { Text = question.Title, FontAttributes = FontAttributes.Bold, FontSize = 16 };
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level2);

        var card = new VerticalStackLayout { Spacing = 6 };
        card.Add(title);
        if (question.Message is { } message)
        {
            card.Add(new Label { Text = message });
        }

        if (question.Context() is { Count: > 0 } context)
        {
            card.Add(Muted(string.Join(" · ", context)));
        }

        if (question.ExpiresAt is { } expiresAt)
        {
            card.Add(Muted($"Answer by {expiresAt.ToLocalTime():t}"));
        }

        if (question.Draft is { } draft)
        {
            foreach (var field in draft.Schema.Fields)
            {
                _form.Add(Build(draft, field));
            }
        }

        card.Add(_form);
        card.Add(_notice);
        _skip.Clicked += (_, _) =>
        {
            Question.Skipped = !Question.Skipped;
            Refresh(DateTimeOffset.UtcNow);
        };
        card.Add(_skip);

        Content = new Border { Padding = 12, StrokeShape = new RoundRectangle { CornerRadius = 8 }, Content = card };
        Refresh(DateTimeOffset.UtcNow);
    }

    public FormQuestion Question { get; }

    /// <summary>Shows every field's problem, as a send attempt does.</summary>
    public void ShowAllErrors()
    {
        if (Question.Draft is { } draft)
        {
            foreach (var field in draft.Schema.Fields)
            {
                ShowError(draft, field);
            }
        }
    }

    /// <summary>Moves focus to the first field with a problem and reads the problem aloud.</summary>
    public void FocusFirstInvalid()
    {
        if (Question.Draft is not { } draft
            || draft.Schema.Fields.FirstOrDefault(f => draft.Check(f.Name).Count > 0) is not { } field)
        {
            return;
        }

        _fields[field.Name].Focus.Focus();
        SemanticScreenReader.Announce(draft.Check(field.Name)[0].Message);
    }

    /// <summary>Re-applies the question's state: expired and skipped questions can only go back abandoned.</summary>
    public void Refresh(DateTimeOffset now)
    {
        var expired = Question.IsExpired(now);
        _form.IsEnabled = !Question.CanOnlySkip(now);
        _form.IsVisible = !expired && !Question.Skipped;
        _notice.IsVisible = Question.CanOnlySkip(now);
        _notice.Text = expired ? "This question expired, so Beacon can't take an answer to it any more. It will be skipped when you send."
            : Question.Skipped ? "Skipped. Beacon will carry on without it."
            : "Beacon Field can't show a form for this question, so it can only be skipped.";
        _skip.IsVisible = !expired && Question.Draft is not null;
        _skip.Text = Question.Skipped ? "Answer it after all" : "I can't answer this";
    }

    private View Build(FormDraft draft, FormField field)
    {
        var error = new Label { TextColor = Colors.Firebrick, FontAttributes = FontAttributes.Bold, IsVisible = false };
        var stack = new VerticalStackLayout { Spacing = 2 };

        switch (field.Kind)
        {
            case FieldKind.Boolean:
                var box = new CheckBox { IsChecked = draft.Flag(field.Name) };
                box.CheckedChanged += (_, e) => draft.SetFlag(field.Name, e.Value);
                SemanticProperties.SetDescription(box, field.Label);
                stack.Add(new HorizontalStackLayout { Spacing = 4, Children = { box, new Label { Text = field.Label, VerticalOptions = LayoutOptions.Center } } });
                _fields[field.Name] = (box, error);
                break;

            case FieldKind.Choice:
                stack.Add(Heading(field));
                RadioButton? first = null;
                foreach (var option in field.Options)
                {
                    var radio = new RadioButton
                    {
                        Content = char.ToUpperInvariant(option[0]) + option[1..],
                        Value = option,
                        GroupName = $"{Question.Id}-{field.Name}",
                        IsChecked = draft.Text(field.Name) == option,
                    };
                    radio.CheckedChanged += (_, e) =>
                    {
                        if (e.Value)
                        {
                            draft.SetText(field.Name, option);
                            draft.Touch(field.Name);
                            ShowError(draft, field);
                        }
                    };
                    SemanticProperties.SetDescription(radio, $"{field.Label}: {option}");
                    stack.Add(radio);
                    first ??= radio;
                }

                _fields[field.Name] = (first!, error);
                break;

            default:
                // Text, numbers and integers are typed; numbers get the numeric keyboard, and Ch15.Forms parses them.
                var entry = new Entry
                {
                    Text = draft.Text(field.Name),
                    Keyboard = field.Kind is FieldKind.Number or FieldKind.Integer ? Keyboard.Numeric : Keyboard.Plain,
                    IsSpellCheckEnabled = false,
                    IsTextPredictionEnabled = false,
                    MaxLength = field.MaxLength ?? int.MaxValue,
                };
                entry.TextChanged += (_, e) =>
                {
                    draft.SetText(field.Name, e.NewTextValue);
                    if (draft.IsTouched(field.Name))
                    {
                        ShowError(draft, field);
                    }
                };
                entry.Unfocused += (_, _) =>
                {
                    draft.Touch(field.Name);
                    ShowError(draft, field);
                };
                SemanticProperties.SetDescription(entry, field.Label);
                if (field.Description is { } hint)
                {
                    SemanticProperties.SetHint(entry, hint);
                }

                stack.Add(Heading(field));
                stack.Add(entry);
                _fields[field.Name] = (entry, error);
                break;
        }

        if (field.Description is { } description && field.Kind != FieldKind.Boolean)
        {
            stack.Add(Muted(description));
        }

        stack.Add(error);
        return stack;
    }

    // Visible once the field was left or a send was attempted; recomputed from the draft every time.
    private void ShowError(FormDraft draft, FormField field)
    {
        var (_, error) = _fields[field.Name];
        var problem = draft.IsTouched(field.Name) ? draft.Check(field.Name).FirstOrDefault()?.Message : null;
        error.Text = problem;
        error.IsVisible = problem is not null;
    }

    private static Label Heading(FormField field) => new()
    {
        Text = field.Required ? field.Label : $"{field.Label} (optional)",
        FontAttributes = FontAttributes.Bold,
    };

    private static Label Muted(string text) => new() { Text = text, FontSize = 13, Opacity = 0.75 };
}
