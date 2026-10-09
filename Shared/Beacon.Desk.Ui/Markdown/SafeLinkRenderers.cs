using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax.Inlines;

namespace Beacon.Desk.Ui;

/// <summary>Renders <c>[text](url)</c> and <c>![alt](url)</c> under <see cref="SafeMarkdown"/>'s rules.</summary>
internal sealed class SafeLinkInlineRenderer : HtmlObjectRenderer<LinkInline>
{
    protected override void Write(HtmlRenderer renderer, LinkInline link)
    {
        if (link.IsImage)
        {
            // Never load an image the model chose. The request alone can carry data out in its URL,
            // so show the alt text instead.
            renderer.Write("<span class=\"bd-md-image\">[image: ");
            renderer.WriteChildren(link);
            renderer.Write("]</span>");
            return;
        }

        var url = link.GetDynamicUrl?.Invoke() ?? link.Url;
        if (!SafeMarkdown.IsAllowedUrl(url))
        {
            // Keep the words, drop the link.
            renderer.WriteChildren(link);
            return;
        }

        renderer.Write("<a href=\"").WriteEscapeUrl(url).Write('"');
        if (!string.IsNullOrEmpty(link.Title))
        {
            renderer.Write(" title=\"").WriteEscape(link.Title).Write('"');
        }

        renderer.Write(" rel=\"noopener noreferrer nofollow\" target=\"_blank\">");
        renderer.WriteChildren(link);
        renderer.Write("</a>");
    }
}

/// <summary>Renders <c>&lt;https://...&gt;</c> autolinks under <see cref="SafeMarkdown"/>'s rules.</summary>
internal sealed class SafeAutolinkInlineRenderer : HtmlObjectRenderer<AutolinkInline>
{
    protected override void Write(HtmlRenderer renderer, AutolinkInline link)
    {
        var url = link.IsEmail ? "mailto:" + link.Url : link.Url;
        if (!SafeMarkdown.IsAllowedUrl(url))
        {
            renderer.WriteEscape(link.Url);
            return;
        }

        renderer.Write("<a href=\"").WriteEscapeUrl(url).Write("\" rel=\"noopener noreferrer nofollow\" target=\"_blank\">");
        renderer.WriteEscape(link.Url);
        renderer.Write("</a>");
    }
}
