using Beacon.Desk.Ui;
using Bunit;

namespace Ch06.Tests;

/// <summary>The transcript's accessibility contract: what screen readers hear, and when.</summary>
public sealed class ChatTranscriptTests : BunitContext
{
    private static readonly TranscriptMessage Question = new("user-1", ChatAuthor.User, "What's the status of WO-1001?");
    private static readonly TranscriptMessage Answer = new("msg-0002", ChatAuthor.Assistant, "**WO-1001** is Scheduled.");

    [Fact]
    public void The_transcript_has_a_polite_live_region_that_starts_empty()
    {
        var transcript = Render<ChatTranscript>(p => p.Add(x => x.Messages, [Question]));

        var live = transcript.Find("[aria-live]");
        Assert.Equal("polite", live.GetAttribute("aria-live"));
        Assert.Equal("true", live.GetAttribute("aria-atomic"));
        Assert.Equal(string.Empty, live.TextContent.Trim());
        Assert.Equal("Conversation", transcript.Find("[role=region]").GetAttribute("aria-label"));
    }

    [Fact]
    public void A_finished_answer_is_announced_once_as_plain_text()
    {
        var transcript = Render<ChatTranscript>(p => p
            .Add(x => x.Messages, [Question])
            .Add(x => x.Streaming, Answer with { Text = "**WO-1001** is" }));

        // While streaming, the message is marked busy and nothing is announced.
        Assert.Equal("true", transcript.Find(".bd-message--streaming").GetAttribute("aria-busy"));
        Assert.Equal(string.Empty, transcript.Find("[aria-live]").TextContent.Trim());

        transcript.Render(p => p.Add(x => x.Messages, [Question, Answer]).Add(x => x.Streaming, null));
        Assert.Equal("Beacon: WO-1001 is Scheduled.", transcript.Find("[aria-live]").TextContent.Trim());

        // A later user message doesn't repeat the announcement.
        transcript.Render(p => p.Add(x => x.Messages, [Question, Answer, Question with { Id = "user-2", Text = "Thanks" }]));
        Assert.Equal("Beacon: WO-1001 is Scheduled.", transcript.Find("[aria-live]").TextContent.Trim());
    }

    [Fact]
    public void A_restored_conversation_is_not_read_aloud()
    {
        var transcript = Render<ChatTranscript>(p => p.Add(x => x.Messages, [Question, Answer]));

        Assert.Equal(string.Empty, transcript.Find("[aria-live]").TextContent.Trim());
    }

    [Fact]
    public void A_stopped_answer_says_so_on_screen_and_aloud()
    {
        var transcript = Render<ChatTranscript>(p => p.Add(x => x.Messages, [Question]));

        transcript.Render(p => p.Add(x => x.Messages, [Question, Answer with { IsPartial = true }]));

        Assert.Contains("Stopped before Beacon finished.", transcript.Find(".bd-message--partial").TextContent);
        Assert.Equal("Beacon: WO-1001 is Scheduled. (stopped)", transcript.Find("[aria-live]").TextContent.Trim());
    }

    [Fact]
    public void The_typing_indicator_has_a_text_alternative()
    {
        var transcript = Render<ChatTranscript>(p => p.Add(x => x.Messages, [Question]).Add(x => x.IsResponding, true));

        var pending = transcript.Find(".bd-message--pending");
        Assert.Equal("true", pending.QuerySelector(".bd-typing")!.GetAttribute("aria-hidden"));
        Assert.Equal("Beacon is working", pending.QuerySelector(".bd-visually-hidden")!.TextContent);
    }
}
