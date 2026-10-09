using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Ch08.Field.Core.Presentation;

namespace Ch08.Beacon.Wpf;

/// <summary>
/// An attached property that fills a <see cref="TextBlock"/> with Markdown runs: WPF's Run, Bold and
/// Italic, never a browser control and never a clickable link.
/// </summary>
public static class InlineText
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.RegisterAttached(
        "Runs", typeof(IReadOnlyList<InlineRun>), typeof(InlineText), new PropertyMetadata(null, OnRunsChanged));

    public static IReadOnlyList<InlineRun>? GetRuns(DependencyObject element) => (IReadOnlyList<InlineRun>?)element.GetValue(RunsProperty);

    public static void SetRuns(DependencyObject element, IReadOnlyList<InlineRun>? value) => element.SetValue(RunsProperty, value);

    private static void OnRunsChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        foreach (var run in e.NewValue as IReadOnlyList<InlineRun> ?? [])
        {
            var inline = new Run(run.Text);
            if (run.Style.HasFlag(InlineStyle.Bold))
            {
                inline.FontWeight = FontWeights.SemiBold;
            }

            if (run.Style.HasFlag(InlineStyle.Italic))
            {
                inline.FontStyle = FontStyles.Italic;
            }

            if (run.Style.HasFlag(InlineStyle.Code))
            {
                inline.FontFamily = new FontFamily("Consolas");
            }

            if (run.Style.HasFlag(InlineStyle.Heading))
            {
                inline.FontSize = block.FontSize * 1.25;
            }

            if (run.Style.HasFlag(InlineStyle.Link))
            {
                inline.TextDecorations = TextDecorations.Underline;
            }

            block.Inlines.Add(inline);
            if (run.Url is { } url)
            {
                block.Inlines.Add(new Run($" ({url})") { Foreground = Brushes.Gray });
            }
        }
    }
}
