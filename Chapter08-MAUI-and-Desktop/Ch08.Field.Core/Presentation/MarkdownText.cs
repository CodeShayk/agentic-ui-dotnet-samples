using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Ch08.Field.Core.Presentation;

/// <summary>A run of text with one style, ready for a native text control (a MAUI <c>Span</c> or a WPF <c>Run</c>).</summary>
/// <param name="Text">The text, shown literally.</param>
/// <param name="Style">How it looks.</param>
public sealed record InlineRun(string Text, InlineStyle Style = InlineStyle.None)
{
    /// <summary>For a link, its address, shown as text after the link and never opened by the app.</summary>
    public string? Url { get; init; }
}

/// <summary>Styles a native renderer applies to a run.</summary>
[Flags]
public enum InlineStyle
{
    /// <summary>Plain text.</summary>
    None = 0,

    /// <summary>Strong emphasis.</summary>
    Bold = 1,

    /// <summary>Emphasis.</summary>
    Italic = 2,

    /// <summary>Inline or block code: a monospaced font.</summary>
    Code = 4,

    /// <summary>A heading.</summary>
    Heading = 8,

    /// <summary>A link's text.</summary>
    Link = 16,
}

/// <summary>
/// Turns model-written Markdown into styled text runs for native controls. Nothing becomes markup: HTML
/// in the input is shown as the text it is, images become their description and links show their
/// address beside their text. A native label can't run script, so the risk Chapter 6's sanitizer guards
/// against in a browser doesn't arise; the remaining one, a convincing link, is handled by never making
/// links tappable.
/// </summary>
public static class MarkdownText
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    /// <summary>Converts <paramref name="markdown"/> to runs. Paragraphs are separated by blank lines.</summary>
    /// <param name="markdown">Untrusted Markdown, possibly incomplete while it streams.</param>
    public static IReadOnlyList<InlineRun> ToRuns(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        List<InlineRun> runs = [];
        if (markdown.Length == 0)
        {
            return runs;
        }

        var document = Markdown.Parse(markdown, Pipeline);
        var first = true;
        foreach (var block in document)
        {
            if (!first)
            {
                runs.Add(new InlineRun("\n\n"));
            }

            first = false;
            AddBlock(runs, block, prefix: string.Empty);
        }

        return Merge(runs);
    }

    /// <summary>The runs' text without styles, for screen readers and the console.</summary>
    /// <param name="markdown">Untrusted Markdown.</param>
    public static string ToPlainText(string markdown) =>
        string.Concat(ToRuns(markdown).Select(r => r.Url is null ? r.Text : $"{r.Text} ({r.Url})"));

    private static void AddBlock(List<InlineRun> runs, Block block, string prefix)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AddInlines(runs, heading.Inline, InlineStyle.Heading | InlineStyle.Bold);
                break;

            case ParagraphBlock paragraph:
                if (prefix.Length > 0)
                {
                    runs.Add(new InlineRun(prefix));
                }

                AddInlines(runs, paragraph.Inline, InlineStyle.None);
                break;

            case ListBlock list:
                var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                var firstItem = true;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    if (!firstItem)
                    {
                        runs.Add(new InlineRun("\n"));
                    }

                    firstItem = false;
                    var bullet = list.IsOrdered ? $"{number++}. " : "• ";
                    var firstChild = true;
                    foreach (var child in item)
                    {
                        if (!firstChild)
                        {
                            runs.Add(new InlineRun("\n"));
                        }

                        AddBlock(runs, child, firstChild ? prefix + bullet : prefix + "   ");
                        firstChild = false;
                    }
                }

                break;

            case Table table:
                var firstRow = true;
                foreach (var row in table.OfType<TableRow>())
                {
                    if (!firstRow)
                    {
                        runs.Add(new InlineRun("\n"));
                    }

                    firstRow = false;
                    var firstCell = true;
                    foreach (var cell in row.OfType<TableCell>())
                    {
                        if (!firstCell)
                        {
                            runs.Add(new InlineRun(" · "));
                        }

                        firstCell = false;
                        foreach (var cellBlock in cell.OfType<LeafBlock>())
                        {
                            AddInlines(runs, cellBlock.Inline, row.IsHeader ? InlineStyle.Bold : InlineStyle.None);
                        }
                    }
                }

                break;

            case QuoteBlock quote:
                var firstQuoted = true;
                foreach (var child in quote)
                {
                    if (!firstQuoted)
                    {
                        runs.Add(new InlineRun("\n"));
                    }

                    firstQuoted = false;
                    AddBlock(runs, child, prefix + "│ ");
                }

                break;

            case CodeBlock code: // fenced or indented, and raw HTML blocks (HtmlBlock derives from LeafBlock, below)
                runs.Add(new InlineRun(code.Lines.ToString().TrimEnd('\n'), InlineStyle.Code));
                break;

            case ThematicBreakBlock:
                runs.Add(new InlineRun("———"));
                break;

            case LeafBlock leaf when leaf.Inline is null:
                // An HTML block, among others: shown as the text it is, never interpreted.
                runs.Add(new InlineRun(leaf.Lines.ToString().TrimEnd('\n')));
                break;

            case LeafBlock leaf:
                AddInlines(runs, leaf.Inline, InlineStyle.None);
                break;

            case ContainerBlock container:
                foreach (var child in container)
                {
                    AddBlock(runs, child, prefix);
                }

                break;
        }
    }

    private static void AddInlines(List<InlineRun> runs, ContainerInline? container, InlineStyle style)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    runs.Add(new InlineRun(literal.Content.ToString(), style));
                    break;

                case EmphasisInline emphasis:
                    AddInlines(runs, emphasis, style | (emphasis.DelimiterCount >= 2 ? InlineStyle.Bold : InlineStyle.Italic));
                    break;

                case CodeInline code:
                    runs.Add(new InlineRun(code.Content, style | InlineStyle.Code));
                    break;

                case LinkInline { IsImage: true } image:
                    // Never fetched: an image URL is a way to send data somewhere. Its description is enough.
                    runs.Add(new InlineRun($"[image: {TextOf(image)}]", style));
                    break;

                case LinkInline link:
                    var text = TextOf(link);
                    runs.Add(new InlineRun(text.Length > 0 ? text : link.Url ?? string.Empty, style | InlineStyle.Link) { Url = link.Url });
                    break;

                case AutolinkInline autolink:
                    runs.Add(new InlineRun(autolink.Url, style | InlineStyle.Link) { Url = autolink.Url });
                    break;

                case LineBreakInline lineBreak:
                    runs.Add(new InlineRun(lineBreak.IsHard ? "\n" : " ", style));
                    break;

                case HtmlEntityInline entity:
                    runs.Add(new InlineRun(entity.Transcoded.ToString(), style));
                    break;

                case HtmlInline html:
                    runs.Add(new InlineRun(html.Tag, style)); // shown, not interpreted
                    break;

                case ContainerInline nested:
                    AddInlines(runs, nested, style);
                    break;
            }
        }
    }

    private static string TextOf(ContainerInline container)
    {
        var text = new StringBuilder();
        foreach (var inline in container.Descendants<LiteralInline>())
        {
            text.Append(inline.Content.ToString());
        }

        return text.ToString();
    }

    // Adjacent runs with the same style become one, so a renderer creates fewer spans.
    private static List<InlineRun> Merge(List<InlineRun> runs)
    {
        List<InlineRun> merged = [];
        foreach (var run in runs)
        {
            if (run.Text.Length == 0)
            {
                continue;
            }

            if (merged.Count > 0 && merged[^1] is { Url: null } last && run.Url is null && last.Style == run.Style)
            {
                merged[^1] = last with { Text = last.Text + run.Text };
            }
            else
            {
                merged.Add(run);
            }
        }

        return merged;
    }
}
