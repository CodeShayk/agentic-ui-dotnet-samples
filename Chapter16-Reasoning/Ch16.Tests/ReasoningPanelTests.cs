using Bunit;
using Ch16.Beacon.Desk.Components.Chat;
using Ch16.Beacon.Desk.Components.Reasoning;

namespace Ch16.Tests;

/// <summary>The "How I got here" panel and its announcer, rendered with bUnit.</summary>
public sealed class ReasoningPanelTests : BunitContext
{
    private static readonly ReplyStep Thought1 = new("r-1", ReplyStepKind.Thought, "Short-cycling with warm air points to the compressor tripping on overload.") { IsSealed = true };
    private static readonly ReplyStep Lookup = new("call-1", ReplyStepKind.Lookup, "Read the fault history for EQ-501");
    private static readonly ReplyStep Thought2 = new("r-2", ReplyStepKind.Thought, "Two high-head-pressure faults point at heat rejection.") { IsSealed = true };

    [Fact]
    public void It_starts_collapsed_with_a_status_line_and_no_reasoning_in_the_markup()
    {
        var panel = Render<ReasoningPanel>(p => p.Add(x => x.Steps, [Thought1, Lookup, Thought2]));

        var toggle = panel.Find("button.ch16-reasoning__toggle");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.True(panel.Find($"#{toggle.GetAttribute("aria-controls")}").HasAttribute("hidden"));
        Assert.Equal("2 thoughts · 1 lookup", panel.Find(".ch16-reasoning__status").TextContent);
        Assert.DoesNotContain("overload", panel.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_line_says_what_Beacon_is_doing_while_it_works()
    {
        var thinking = Render<ReasoningPanel>(p => p.Add(x => x.Steps, [Thought1 with { InProgress = true }]));
        Assert.Equal("Thinking…", thinking.Find(".ch16-reasoning__status").TextContent);

        var looking = Render<ReasoningPanel>(p => p.Add(x => x.Steps, [Thought1, Lookup with { InProgress = true }]));
        Assert.Equal("Read the fault history for EQ-501…", looking.Find(".ch16-reasoning__status").TextContent);
    }

    [Fact]
    public void Expanding_shows_the_steps_and_acknowledges_the_sealed_reasoning_without_showing_it()
    {
        var expanded = false;
        var panel = Render<ReasoningPanel>(p => p
            .Add(x => x.Steps, [Thought1, Lookup, Thought2, new ReplyStep("r-3", ReplyStepKind.Thought, string.Empty) { IsSealed = true }])
            .Bind(x => x.Expanded, expanded, value => expanded = value));

        panel.Find("button").Click();
        panel.Render(p => p.Add(x => x.Expanded, expanded));

        Assert.True(expanded);
        Assert.Equal("true", panel.Find("button").GetAttribute("aria-expanded"));
        Assert.False(panel.Find(".ch16-reasoning__body").HasAttribute("hidden"));
        Assert.Equal(4, panel.FindAll(".ch16-step").Count);
        Assert.Contains("compressor tripping on overload", panel.Find(".ch16-step--thought").TextContent, StringComparison.Ordinal);
        Assert.Equal("Read the fault history for EQ-501", panel.Find(".ch16-step__lookup").TextContent);
        Assert.Equal("Beacon's reasoning for this step isn't shown here.", panel.Find(".ch16-step__withheld").TextContent);
        Assert.Contains("sealed its full reasoning", panel.Find(".ch16-reasoning__sealed").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Reasoning_is_read_out_only_from_an_open_panel_and_only_as_it_finishes()
    {
        HashSet<string> expanded = [];
        var streaming = new Reply("reply:r-1", [Thought1 with { InProgress = true }], string.Empty) { IsStreaming = true };
        var announcer = Render<ReasoningAnnouncer>(p => p.Add(x => x.Replies, [streaming]).Add(x => x.Expanded, expanded));

        // The first thought finishes while the panel is collapsed: nothing is said.
        announcer.Render(p => p.Add(x => x.Replies, [streaming with { Steps = [Thought1] }]));
        Assert.Equal(string.Empty, announcer.Find("[aria-live]").TextContent.Trim());

        // Dana opens the panel. The finished thought isn't replayed; she can read it in place.
        expanded.Add("reply:r-1");
        announcer.Render(p => p.Add(x => x.Replies, [streaming with { Steps = [Thought1, Lookup, Thought2 with { InProgress = true }] }]).Add(x => x.Expanded, expanded));
        Assert.Equal(string.Empty, announcer.Find("[aria-live]").TextContent.Trim());

        // The next thought finishes while it's open: that one is announced.
        announcer.Render(p => p.Add(x => x.Replies, [streaming with { Steps = [Thought1, Lookup, Thought2] }]).Add(x => x.Expanded, expanded));
        Assert.Equal("How I got here: Two high-head-pressure faults point at heat rejection.", announcer.Find("[aria-live]").TextContent.Trim());
    }
}
