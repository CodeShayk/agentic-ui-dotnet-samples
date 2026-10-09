using System.Net;
using Beacon.Client.Core;
using Bunit;
using Ch15.Beacon.Desk.Components;
using Ch15.Beacon.Desk.Components.Questions;
using Ch15.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ch15.Tests;

/// <summary>The Desk's generated forms: labels, inline validation, focus, skipping, expiry and refused answers.</summary>
public sealed class InterruptPanelTests : BunitContext
{
    // Dana's browser reports US Eastern time; on Beacon's Monday that is UTC-5.
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.CreateCustomTimeZone("Test/Eastern", TimeSpan.FromHours(-5), "Eastern", "Eastern");

    private readonly FakeTimeProvider _clock = new(AgentHost.Start);

    public InterruptPanelTests()
    {
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton(new DispatcherTime(Eastern));
        JSInterop.Mode = JSRuntimeMode.Loose; // FocusAsync goes through JS interop
    }

    private static InterruptView Serial { get; } = new("inp_serial", InterruptKind.Input)
    {
        Reason = "input_required",
        Message = "EQ-601 has no serial number on record. Read it from the nameplate.",
        ResponseSchema = Wire.Json(FormSchemaTests.SerialSchema),
        ExpiresAt = "2026-11-16T16:35:00Z",
        Metadata = Wire.Json("""{"beacon":{"form":"serial-number","equipmentId":"EQ-601","model":"HP-24S"}}"""),
    };

    private static InterruptView Reading { get; } = new("inp_reading", InterruptKind.Input)
    {
        Reason = "input_required",
        Message = "Measure the outdoor fan motor on EQ-601.",
        ResponseSchema = Wire.Json(FormSchemaTests.ReadingSchema),
        ExpiresAt = "2026-11-16T16:40:00Z",
        Metadata = Wire.Json("""{"beacon":{"form":"fan-motor-reading","equipmentId":"EQ-601","model":"HP-24S","ratedAmps":1.4}}"""),
    };

    [Fact]
    public void Every_field_gets_a_labelled_control_with_its_hint()
    {
        var panel = Render<InterruptPanel>(p => p.Add(x => x.Interrupts, [Serial, Reading]));

        Assert.Equal("Beacon needs 2 answers", panel.Find("h3").TextContent);
        Assert.Equal("Serial number for EQ-601", panel.Find("#inp_serial-title").TextContent);
        Assert.Contains("EQ-601 · HP-24S · Rated current: 1.4 A", panel.Markup, StringComparison.Ordinal);

        var serial = panel.Find("#inp_serial-serialNumber");
        Assert.Equal("Serial number", panel.Find("label[for=inp_serial-serialNumber]").TextContent.Trim());
        Assert.Equal(("15", "true", "inp_serial-serialNumber-hint"), (serial.GetAttribute("maxlength"), serial.GetAttribute("aria-required"), serial.GetAttribute("aria-describedby")));
        Assert.Equal("As printed on the nameplate: HP24S-####-####.", panel.Find("#inp_serial-serialNumber-hint").TextContent);

        Assert.StartsWith("Where did you read it?", panel.Find("fieldset legend").TextContent.Trim());
        Assert.Equal(3, panel.FindAll("input[type=radio][name=inp_serial-readFrom]").Count);
        Assert.Equal("I photographed the nameplate", panel.Find("label[for=inp_serial-nameplatePhotoTaken]").TextContent);
        Assert.Equal("decimal", panel.Find("#inp_reading-amps").GetAttribute("inputmode"));
    }

    [Fact]
    public void The_heading_takes_focus_when_questions_arrive()
    {
        Render<InterruptPanel>(p => p.Add(x => x.Interrupts, [Serial]));

        JSInterop.VerifyFocusAsyncInvoke();
    }

    [Fact]
    public void An_error_appears_inline_once_the_field_is_left_and_clears_when_fixed()
    {
        var panel = Render<InterruptPanel>(p => p.Add(x => x.Interrupts, [Serial]));

        panel.Find("#inp_serial-serialNumber").Change("HP24S-22O7-1184");
        Assert.Empty(panel.FindAll("#inp_serial-serialNumber-error")); // not before the field is left

        panel.Find("#inp_serial-serialNumber").Blur();
        Assert.Equal("Serial number isn't in the expected format.", panel.Find("#inp_serial-serialNumber-error").TextContent);
        Assert.Equal("true", panel.Find("#inp_serial-serialNumber").GetAttribute("aria-invalid"));
        Assert.Equal("inp_serial-serialNumber-hint inp_serial-serialNumber-error", panel.Find("#inp_serial-serialNumber").GetAttribute("aria-describedby"));

        panel.Find("#inp_serial-serialNumber").Change("HP24S-2207-1184");
        Assert.Empty(panel.FindAll("#inp_serial-serialNumber-error"));
        Assert.False(panel.Find("#inp_serial-serialNumber").HasAttribute("aria-invalid"));
    }

    [Fact]
    public void Sending_with_problems_raises_nothing_shows_every_error_and_focuses_the_first()
    {
        QuestionAnswers? sent = null;
        var panel = Render<InterruptPanel>(p => p.Add(x => x.Interrupts, [Serial, Reading]).Add(x => x.OnSubmit, a => sent = a));

        panel.Find("#inp_reading-amps").Change("2.4");
        panel.Find(".ch15-actions button").Click();

        Assert.Null(sent);
        Assert.Equal("Some answers need fixing before Beacon can use them.", panel.Find("[role=alert]").TextContent);
        Assert.Equal(["Serial number is required.", "Where did you read it? is required.", "What does it sound like? is required."],
            panel.FindAll(".ch15-question .ch15-error").Select(e => e.TextContent));
        Assert.Equal(2, JSInterop.Invocations.Count(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase))); // heading, then the field
    }

    [Fact]
    public void Answers_and_skips_come_back_as_one_decision_covering_every_question()
    {
        QuestionAnswers? sent = null;
        var panel = Render<InterruptPanel>(p => p.Add(x => x.Interrupts, [Serial, Reading]).Add(x => x.OnSubmit, a => sent = a));

        panel.Find("#inp_serial-serialNumber").Change("HP24S-2207-1184");
        panel.Find("input[name=inp_serial-readFrom][value=nameplate]").Change("nameplate");
        panel.Find("#inp_serial-nameplatePhotoTaken").Change(true);
        panel.FindAll(".ch15-question")[1].QuerySelector("button")!.Click(); // "I can't answer this"
        Assert.Contains("Skipped. Beacon will carry on without it.", panel.Markup, StringComparison.Ordinal);

        panel.Find(".ch15-actions button").Click();

        Assert.NotNull(sent);
        var answer = Assert.Single(sent.Answers);
        Assert.Equal("inp_serial", answer.InterruptId);
        Assert.Equal("""{"serialNumber":"HP24S-2207-1184","readFrom":"nameplate","nameplatePhotoTaken":true}""", answer.Payload.GetRawText());
        Assert.Equal(["inp_reading"], sent.Abandoned);
    }

    [Fact]
    public void A_question_turns_skip_only_when_it_expires_without_anyone_touching_the_page()
    {
        var panel = Render<InterruptPanel>(p => p.Add(x => x.Interrupts, [Serial, Reading]));
        Assert.Equal("Send answers", panel.Find(".ch15-actions button").TextContent);

        _clock.Advance(TimeSpan.FromMinutes(15)); // the serial number question expires at 16:35

        panel.WaitForAssertion(() =>
        {
            Assert.Single(panel.FindAll(".ch15-notice[role=status]"));
            Assert.Empty(panel.FindAll("#inp_serial-serialNumber"));
            Assert.Single(panel.FindAll("#inp_reading-amps"));
        });

        _clock.Advance(TimeSpan.FromMinutes(5));

        panel.WaitForAssertion(() =>
        {
            Assert.Equal(2, panel.FindAll(".ch15-notice[role=status]").Count);
            Assert.Equal("Skip and carry on", panel.Find(".ch15-actions button").TextContent);
        });
    }

    [Fact]
    public void Answer_by_times_are_in_the_dispatchers_zone_and_a_warning_comes_two_minutes_before()
    {
        var panel = Render<InterruptPanel>(p => p.Add(x => x.Interrupts, [Serial]));
        Assert.Equal("15 minutes (by 11:35 AM)", panel.Find("time").TextContent);
        Assert.Equal(string.Empty, panel.Find(".ch15-questions__status").TextContent);

        _clock.Advance(TimeSpan.FromMinutes(13));

        panel.WaitForAssertion(() =>
        {
            Assert.Equal("2 minutes (by 11:35 AM)", panel.Find("time").TextContent);
            Assert.StartsWith("Two minutes or less left to answer.", panel.Find(".ch15-questions__status[role=status]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Until_the_agent_takes_the_answers_the_panel_stays_and_says_it_is_waiting()
    {
        var accepted = new TaskCompletionSource();
        var panel = Render<InterruptPanel>(p => p.Add(x => x.Interrupts, [Serial]).Add(x => x.OnSubmit, _ => accepted.Task));
        panel.FindAll(".ch15-question button").Single(b => b.TextContent == "I can't answer this").Click();

        panel.Find(".ch15-actions button").Click();

        Assert.Equal("Sending your answers. They count once Beacon starts on them.", panel.Find(".ch15-questions__status").TextContent);
        Assert.True(panel.Find(".ch15-actions button").HasAttribute("disabled"));
        accepted.SetResult();
    }

    [Fact]
    public void Refused_answers_come_back_with_the_reason_and_what_was_typed()
    {
        var panel = Render<InterruptPanel>(p => p.Add(x => x.Interrupts, [Serial]));
        panel.Find("#inp_serial-serialNumber").Change("HP24S-2207-1184");

        // The agent refused the resume with a 409: the conversation keeps the question open and says why.
        var refused = Serial with { LastAnswerFailure = new RunFailure(FailureKind.Rejected, "refused") { StatusCode = HttpStatusCode.Conflict } };
        panel.Render(p => p.Add(x => x.Interrupts, [refused]));

        Assert.StartsWith("Beacon didn't accept those answers.", panel.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Equal("HP24S-2207-1184", panel.Find("#inp_serial-serialNumber").GetAttribute("value"));
        Assert.Equal(2, JSInterop.Invocations.Count(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase))); // on arrival, and again
    }

    [Fact]
    public void After_a_409_the_panel_offers_its_own_New_chat_but_not_after_a_400()
    {
        var newChats = 0;
        var conflict = Serial with { LastAnswerFailure = new RunFailure(FailureKind.Rejected, "refused") { StatusCode = HttpStatusCode.Conflict } };
        var panel = Render<InterruptPanel>(p => p.Add(x => x.Interrupts, [conflict]).Add(x => x.OnNewChat, () => newChats++));

        // The chat's own New chat waits while questions are open, so this is the way on if Beacon refuses a skip too.
        Assert.Equal(["Send answers", "New chat"], panel.FindAll(".ch15-actions button").Select(b => b.TextContent));
        panel.FindAll(".ch15-actions button")[1].Click();
        Assert.Equal(1, newChats);

        var invalid = Serial with { LastAnswerFailure = new RunFailure(FailureKind.Rejected, "refused") { StatusCode = HttpStatusCode.BadRequest } };
        panel.Render(p => p.Add(x => x.Interrupts, [invalid]));
        Assert.Equal(["Send answers"], panel.FindAll(".ch15-actions button").Select(b => b.TextContent));
    }

    [Fact]
    public void A_schema_outside_the_subset_is_not_rendered_and_can_only_be_skipped()
    {
        QuestionAnswers? sent = null;
        var odd = Serial with { Id = "inp_odd", ResponseSchema = Wire.Json("""{"type":"object","properties":{"site":{"oneOf":[{"type":"string"}]}}}""") };
        var panel = Render<InterruptPanel>(p => p.Add(x => x.Interrupts, [odd]).Add(x => x.OnSubmit, a => sent = a));

        Assert.Contains("Beacon Desk can't show a form for this question", panel.Markup, StringComparison.Ordinal);
        Assert.Empty(panel.FindAll("input"));

        panel.Find(".ch15-actions button").Click();
        Assert.Equal(["inp_odd"], sent!.Abandoned);
    }
}
