using System.Globalization;
using Ch08.Field.Core.Presentation;

namespace Ch08.Beacon.Field;

/// <summary>
/// Turns Markdown runs into a <see cref="FormattedString"/>: native spans, no HTML, nothing tappable.
/// A label rendered this way can't run script or load an image, whatever the model wrote.
/// </summary>
public sealed class InlineRunsConverter : IValueConverter
{
    private static readonly string CodeFont =
        DeviceInfo.Current.Platform == DevicePlatform.WinUI ? "Consolas"
        : DeviceInfo.Current.Platform == DevicePlatform.Android ? "monospace"
        : "Menlo";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var formatted = new FormattedString();
        if (value is not IReadOnlyList<InlineRun> runs)
        {
            return formatted;
        }

        foreach (var run in runs)
        {
            var span = new Span
            {
                Text = run.Text,
                FontAttributes = (run.Style.HasFlag(InlineStyle.Bold) ? FontAttributes.Bold : FontAttributes.None)
                    | (run.Style.HasFlag(InlineStyle.Italic) ? FontAttributes.Italic : FontAttributes.None),
            };

            if (run.Style.HasFlag(InlineStyle.Code))
            {
                span.FontFamily = CodeFont;
            }

            if (run.Style.HasFlag(InlineStyle.Heading))
            {
                span.FontSize = 18;
            }

            if (run.Style.HasFlag(InlineStyle.Link))
            {
                span.TextDecorations = TextDecorations.Underline;
            }

            formatted.Spans.Add(span);

            // A link shows where it goes, and the app never opens it.
            if (run.Url is { } url)
            {
                formatted.Spans.Add(new Span { Text = $" ({url})", FontSize = 12, TextColor = Colors.Gray });
            }
        }

        return formatted;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Negates a Boolean, for "visible when not busy".</summary>
public sealed class InvertBooleanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Chooses the user's or Beacon's bubble for a transcript entry.</summary>
public sealed class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }

    public DataTemplate? AssistantTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
        (item is MessageItem { IsUser: true } ? UserTemplate : AssistantTemplate)
        ?? throw new InvalidOperationException("Both templates must be set.");
}
