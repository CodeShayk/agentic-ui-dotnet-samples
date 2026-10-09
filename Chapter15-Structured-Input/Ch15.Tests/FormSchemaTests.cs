using System.Text.RegularExpressions;
using Ch15.Forms;

namespace Ch15.Tests;

/// <summary>The JSON Schema subset: what parses, what is refused, and how answers are checked.</summary>
public sealed class FormSchemaTests
{
    // The schema the agent sends for EQ-601's serial number (copied from a captured RUN_FINISHED).
    public const string SerialSchema = """
        {"type":"object","title":"Serial number for EQ-601","properties":{
          "serialNumber":{"type":"string","title":"Serial number","description":"As printed on the nameplate: HP24S-####-####.","pattern":"^HP24S-[0-9]{4}-[0-9]{4}$","minLength":15,"maxLength":15},
          "readFrom":{"type":"string","title":"Where did you read it?","enum":["nameplate","paperwork","customer"]},
          "nameplatePhotoTaken":{"type":"boolean","title":"I photographed the nameplate","default":false}},
         "required":["serialNumber","readFrom"],"additionalProperties":false}
        """;

    public const string ReadingSchema = """
        {"type":"object","title":"Outdoor fan motor on EQ-601","properties":{
          "amps":{"type":"number","title":"Running current (A)","minimum":0,"maximum":10},
          "noise":{"type":"string","title":"What does it sound like?","enum":["grinding","squealing","rattling","quiet"]},
          "shaftPlay":{"type":"boolean","title":"The shaft wobbles when I move it by hand","default":false}},
         "required":["amps","noise"],"additionalProperties":false}
        """;

    public static FormSchema Parse(string json)
    {
        Assert.True(FormSchema.TryParse(Wire.Json(json), out var form, out var problems), string.Join(" ", problems));
        return form;
    }

    [Fact]
    public void The_serial_number_schema_becomes_three_fields_in_order()
    {
        var form = Parse(SerialSchema);

        Assert.Equal("Serial number for EQ-601", form.Title);
        Assert.False(form.AllowsOtherProperties);
        Assert.Collection(form.Fields,
            serial =>
            {
                Assert.Equal(("serialNumber", FieldKind.Text, "Serial number", true), (serial.Name, serial.Kind, serial.Label, serial.Required));
                Assert.Equal(("^HP24S-[0-9]{4}-[0-9]{4}$", 15, 15), (serial.Pattern, serial.MinLength, serial.MaxLength));
                Assert.Equal("As printed on the nameplate: HP24S-####-####.", serial.Description);
            },
            source =>
            {
                Assert.Equal((FieldKind.Choice, true), (source.Kind, source.Required));
                Assert.Equal(["nameplate", "paperwork", "customer"], source.Options);
            },
            photo => Assert.Equal((FieldKind.Boolean, false, false), (photo.Kind, photo.Required, photo.Default!.Value.GetBoolean())));
    }

    [Fact]
    public void A_property_without_a_title_gets_a_readable_label()
    {
        var form = Parse("""{"type":"object","properties":{"ratedAmps":{"type":"number"},"fan_noise":{"type":"string"}}}""");

        Assert.Equal(["Rated amps", "Fan noise"], form.Fields.Select(f => f.Label));
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"a":{"oneOf":[{"type":"string"},{"type":"number"}]}}}""", "/properties/a")]
    [InlineData("""{"type":"object","properties":{"site":{"type":"object","properties":{"lat":{"type":"number"}}}}}""", "/properties/site")]
    [InlineData("""{"type":"object","properties":{"photos":{"type":"array","items":{"type":"string"}}}}""", "/properties/photos")]
    [InlineData("""{"type":"object","properties":{"note":{"type":["string","null"]}}}""", "/properties/note/type")]
    [InlineData("""{"type":"object","properties":{"a":{"$ref":"#/$defs/a"}},"$defs":{"a":{"type":"string"}}}""", "$defs")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":{"type":"string"}}""", "additionalProperties")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"required":["b"]}""", "'b'")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","pattern":"(unclosed"}}}""", "/properties/a/pattern")]
    [InlineData("""{"type":"string"}""", "\"type\": \"object\"")]
    public void A_schema_outside_the_subset_is_refused_with_a_reason(string schema, string mentioned)
    {
        Assert.False(FormSchema.TryParse(Wire.Json(schema), out var form, out var problems));
        Assert.Null(form);
        Assert.Contains(problems, p => p.Contains(mentioned, StringComparison.Ordinal));
    }

    [Fact]
    public void A_valid_answer_has_no_errors()
    {
        var form = Parse(SerialSchema);

        Assert.Empty(form.Validate(Wire.Json(Wire.SerialAnswer)));
        Assert.Empty(form.Validate(Wire.Json("""{"serialNumber":"HP24S-2207-1184","readFrom":"customer"}""")));
    }

    [Theory]
    [InlineData("""{"readFrom":"nameplate"}""", "serialNumber", "Serial number is required.")]
    [InlineData("""{"serialNumber":"HP24S-22O7-1184","readFrom":"nameplate"}""", "serialNumber", "Serial number isn't in the expected format.")]
    [InlineData("""{"serialNumber":"HP24S-2207-118","readFrom":"nameplate"}""", "serialNumber", "Serial number must be at least 15 characters.")]
    [InlineData("""{"serialNumber":"CA36X-2219-0441","readFrom":"nameplate"}""", "serialNumber", "Serial number isn't in the expected format.")]
    [InlineData("""{"serialNumber":42,"readFrom":"nameplate"}""", "serialNumber", "Serial number must be text.")]
    [InlineData("""{"serialNumber":"HP24S-2207-1184","readFrom":"guess"}""", "readFrom", "Where did you read it? must be one of: nameplate, paperwork, customer.")]
    [InlineData("""{"serialNumber":"HP24S-2207-1184","readFrom":"nameplate","nameplatePhotoTaken":"yes"}""", "nameplatePhotoTaken", "I photographed the nameplate must be true or false.")]
    [InlineData("""{"serialNumber":"HP24S-2207-1184","readFrom":"nameplate","note":"hi"}""", "note", "'note' isn't a field of this form.")]
    public void Each_constraint_names_the_field_it_failed(string payload, string field, string message)
    {
        var errors = Parse(SerialSchema).Validate(Wire.Json(payload));

        var error = Assert.Single(errors);
        Assert.Equal((field, message), (error.Field, error.Message));
    }

    [Theory]
    [InlineData("""{"amps":2.4,"noise":"grinding"}""", null)]
    [InlineData("""{"amps":0,"noise":"quiet"}""", null)]
    [InlineData("""{"amps":-0.1,"noise":"quiet"}""", "Running current (A) must be at least 0.")]
    [InlineData("""{"amps":10.5,"noise":"quiet"}""", "Running current (A) must be at most 10.")]
    [InlineData("""{"amps":"2.4","noise":"quiet"}""", "Running current (A) must be a number.")]
    public void Numbers_are_checked_against_their_range(string payload, string? message)
    {
        var errors = Parse(ReadingSchema).Validate(Wire.Json(payload));

        Assert.Equal(message, errors.SingleOrDefault()?.Message);
    }

    [Fact]
    public void Integers_exclusive_bounds_and_the_whole_answer_are_checked()
    {
        var form = Parse("""{"type":"object","properties":{"visits":{"type":"integer","exclusiveMinimum":0,"exclusiveMaximum":5}}}""");

        Assert.Empty(form.Validate(Wire.Json("""{"visits":2.0}""")));
        Assert.Equal("Visits must be a whole number.", Assert.Single(form.Validate(Wire.Json("""{"visits":2.5}"""))).Message);
        Assert.Equal("Visits must be more than 0.", Assert.Single(form.Validate(Wire.Json("""{"visits":0}"""))).Message);
        Assert.Equal("Visits must be less than 5.", Assert.Single(form.Validate(Wire.Json("""{"visits":5}"""))).Message);
        Assert.Equal(string.Empty, Assert.Single(form.Validate(Wire.Json("""["visits"]"""))).Field);
    }

    [Fact]
    public void Patterns_use_ECMAScript_digits_so_other_scripts_do_not_slip_through()
    {
        // "٢٢٠٧" is 2207 in Arabic-Indic digits. .NET's \d would accept them by default; ECMAScript's doesn't.
        var form = Parse("""{"type":"object","properties":{"code":{"type":"string","pattern":"^\\d{4}$"}}}""");

        Assert.Empty(form.Validate(Wire.Json("""{"code":"2207"}""")));
        Assert.Single(form.Validate(Wire.Json("""{"code":"٢٢٠٧"}""")));
    }

    [Fact]
    public void A_dollar_anchor_matches_only_at_the_end_of_the_text_as_in_ECMAScript()
    {
        // .NET's $ also matches before a final newline, ECMAScript mode or not...
        Assert.Matches(new Regex("^HP24S-[0-9]{4}-[0-9]{4}$", RegexOptions.ECMAScript), "HP24S-2207-1184\n");

        // ...so Ch15.Forms rewrites each $ anchor. No maxLength here to catch the newline instead.
        var form = Parse("""{"type":"object","properties":{"code":{"type":"string","pattern":"^[A-Z]+$"},"price":{"type":"string","pattern":"^\\$[0-9]+[$]?$"}}}""");

        Assert.Empty(form.Validate(Wire.Json("""{"code":"HP","price":"$12"}""")));
        Assert.Empty(form.Validate(Wire.Json("""{"price":"$12$"}""")));
        Assert.Equal("Code isn't in the expected format.", Assert.Single(form.Validate(Wire.Json("""{"code":"HP\n"}"""))).Message);
        Assert.Equal("Price isn't in the expected format.", Assert.Single(form.Validate(Wire.Json("""{"price":"$12\n"}"""))).Message);
    }

    [Fact]
    public void A_pathological_pattern_times_out_instead_of_hanging()
    {
        var form = Parse("""{"type":"object","properties":{"a":{"type":"string","pattern":"^(a+)+$"}}}""");

        var errors = form.Validate(Wire.Json($$"""{"a":"{{new string('a', 40)}}!"}"""));

        Assert.Equal("A couldn't be checked.", Assert.Single(errors).Message);
    }
}
