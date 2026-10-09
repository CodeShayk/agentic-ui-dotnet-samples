using Ch15.Forms;

namespace Ch15.Tests;

/// <summary>The editable state a renderer binds to: defaults, typed input, inline checks and the payload.</summary>
public sealed class FormDraftTests
{
    [Fact]
    public void A_draft_starts_from_the_schema_defaults()
    {
        var draft = new FormDraft(FormSchemaTests.Parse(FormSchemaTests.SerialSchema));

        Assert.Equal(string.Empty, draft.Text("serialNumber"));
        Assert.False(draft.Flag("nameplatePhotoTaken"));
        Assert.False(draft.IsTouched("serialNumber"));
    }

    [Fact]
    public void Typed_input_is_checked_with_the_same_rules_as_the_payload()
    {
        var draft = new FormDraft(FormSchemaTests.Parse(FormSchemaTests.SerialSchema));

        draft.SetText("serialNumber", "HP24S-22O7-1184");
        Assert.Equal("Serial number isn't in the expected format.", Assert.Single(draft.Check("serialNumber")).Message);

        draft.SetText("serialNumber", "  HP24S-2207-1184 "); // trimmed before it is checked or sent
        Assert.Empty(draft.Check("serialNumber"));
    }

    [Fact]
    public void A_number_that_does_not_parse_says_so_rather_than_required()
    {
        var draft = new FormDraft(FormSchemaTests.Parse(FormSchemaTests.ReadingSchema));

        draft.SetText("amps", "2,4");

        Assert.Equal("Running current (A) must be a number.", Assert.Single(draft.Check("amps")).Message);
    }

    [Fact]
    public void There_is_no_payload_until_every_field_is_valid()
    {
        var draft = new FormDraft(FormSchemaTests.Parse(FormSchemaTests.ReadingSchema));
        draft.SetText("amps", "2.4");

        Assert.False(draft.TryGetPayload(out _, out var errors));
        Assert.Equal("What does it sound like? is required.", Assert.Single(errors).Message);

        draft.SetText("noise", "grinding");
        draft.SetFlag("shaftPlay", true);
        Assert.True(draft.TryGetPayload(out var payload, out _));
        Assert.Equal("""{"amps":2.4,"noise":"grinding","shaftPlay":true}""", payload.GetRawText());
        Assert.Empty(draft.Schema.Validate(payload));
    }

    [Fact]
    public void Empty_optional_text_is_left_out_and_booleans_are_always_sent()
    {
        var draft = new FormDraft(FormSchemaTests.Parse("""
            {"type":"object","properties":{"note":{"type":"string","maxLength":40},"done":{"type":"boolean"}}}
            """));

        Assert.True(draft.TryGetPayload(out var payload, out _));
        Assert.Equal("""{"done":false}""", payload.GetRawText());
    }

    [Fact]
    public void A_submit_attempt_touches_every_field()
    {
        var draft = new FormDraft(FormSchemaTests.Parse(FormSchemaTests.SerialSchema));

        draft.TouchAll();

        Assert.All(draft.Schema.Fields, f => Assert.True(draft.IsTouched(f.Name)));
        Assert.Throws<ArgumentException>(() => draft.SetText("unknown", "x"));
    }
}
