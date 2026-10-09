using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html.Inlines;

namespace Beacon.Desk.Ui;

/// <summary>
/// Turns Markdown written by a model into HTML that is safe to put in the page. The model's output is
/// untrusted, so the rules are an allow-list applied while the HTML is generated, not a filter run
/// over it afterward: raw HTML in the input is shown as text, links survive only for <c>http</c>,
/// <c>https</c> and <c>mailto</c>, and images are never loaded.
/// </summary>
public static class SafeMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()          // <script>, <img onerror=...> and every other tag become literal text
        .UsePipeTables()        // models like tables for comparisons
        .UseEmphasisExtras()    // ~~strikethrough~~
        .UseAutoLinks()         // bare https:// URLs become links (and still pass the scheme check)
        .Build();

    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    /// <summary>Renders Markdown to sanitized HTML.</summary>
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var document = Markdown.Parse(markdown, Pipeline);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);

        // Links and images are the two places Markdown turns text into a URL the browser acts on.
        if (!renderer.ObjectRenderers.Replace<LinkInlineRenderer>(new SafeLinkInlineRenderer()) ||
            !renderer.ObjectRenderers.Replace<AutolinkInlineRenderer>(new SafeAutolinkInlineRenderer()))
        {
            throw new InvalidOperationException("Markdig's link renderers were not found; refusing to render unsafely.");
        }

        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>Renders Markdown as plain text, for screen-reader announcements and previews.</summary>
    public static string ToPlainText(string? markdown) =>
        string.IsNullOrEmpty(markdown) ? string.Empty : Markdown.ToPlainText(markdown, Pipeline).Trim();

    /// <summary>
    /// Whether a link target may be rendered as a link: an absolute URL with an allowed scheme.
    /// <c>javascript:</c>, <c>data:</c>, <c>vbscript:</c>, <c>file:</c> and relative URLs are refused.
    /// </summary>
    public static bool IsAllowedUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && AllowedSchemes.Contains(uri.Scheme);
}
