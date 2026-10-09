using Ch15.Forms;

namespace Ch15.Tests;

/// <summary>The platform-independent question both renderers share: expiry, skipping and one decision for all.</summary>
public sealed class FormQuestionTests
{
    private static FormQuestion Serial(string expiresAt = "2026-11-16T16:35:00Z") =>
        new("inp_serial", "Read the serial number.", Wire.Json(FormSchemaTests.SerialSchema), expiresAt,
            Wire.Json("""{"beacon":{"equipmentId":"EQ-601","model":"HP-24S"}}"""));

    private static FormQuestion Reading() =>
        new("inp_reading", "Measure the fan motor.", Wire.Json(FormSchemaTests.ReadingSchema), "2026-11-16T16:40:00Z",
            Wire.Json("""{"beacon":{"equipmentId":"EQ-601","model":"HP-24S","ratedAmps":1.4}}"""));

    [Fact]
    public void Expiry_reads_expiresAt_the_way_the_conversation_does()
    {
        var question = Serial();

        Assert.False(question.IsExpired(AgentHost.Start));
        Assert.True(question.IsExpired(AgentHost.Start.AddMinutes(15))); // now-or-earlier is expired
        Assert.False(new FormQuestion("x", null, Wire.Json(FormSchemaTests.SerialSchema), "whenever", null).IsExpired(DateTimeOffset.MaxValue));
    }

    [Fact]
    public void Metadata_under_beacon_becomes_context_for_the_person()
    {
        Assert.Equal(["EQ-601 · HP-24S", "Rated current: 1.4 A"], Reading().Context());
        Assert.Empty(new FormQuestion("x", null, null, null, Wire.Json("""{"other":{"ratedAmps":1.4}}""")).Context());
    }

    [Fact]
    public void A_decision_covers_every_question_answered_or_abandoned()
    {
        var serial = Serial();
        serial.Draft!.SetText("serialNumber", "HP24S-2207-1184");
        serial.Draft.SetText("readFrom", "nameplate");
        var reading = Reading();
        reading.Skipped = true;
        var unrenderable = new FormQuestion("inp_odd", "?", null, null, null);

        Assert.True(FormQuestion.TryDecide([serial, reading, unrenderable], AgentHost.Start, out var decided, out var invalid));

        Assert.Null(invalid);
        Assert.Equal(["inp_serial"], decided.Answers.Select(a => a.InterruptId));
        Assert.Equal(["inp_reading", "inp_odd"], decided.Abandoned);
        Assert.Equal(["inp_serial", "inp_reading", "inp_odd"], decided.InterruptIds);
    }

    [Fact]
    public void An_expired_question_is_abandoned_whatever_was_typed()
    {
        var serial = Serial();
        serial.Draft!.SetText("serialNumber", "HP24S-2207-1184");
        serial.Draft.SetText("readFrom", "nameplate");

        Assert.True(FormQuestion.TryDecide([serial], AgentHost.Start.AddMinutes(20), out var decided, out _));

        Assert.Empty(decided.Answers);
        Assert.Equal(["inp_serial"], decided.Abandoned);
    }

    [Fact]
    public void Any_answer_with_problems_stops_the_whole_decision_and_names_the_first()
    {
        var serial = Serial();
        var reading = Reading();
        reading.Draft!.SetText("amps", "2.4");
        reading.Draft.SetText("noise", "grinding");

        Assert.False(FormQuestion.TryDecide([serial, reading], AgentHost.Start, out _, out var invalid));

        Assert.Same(serial, invalid);
        Assert.True(serial.Draft!.IsTouched("serialNumber")); // its errors now show
    }
}
