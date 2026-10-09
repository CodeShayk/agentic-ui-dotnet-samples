using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.Testing;
using Ch15.Forms;

namespace Ch15.Tests;

/// <summary>What the first run puts on the wire: two input interrupts, built from the server's records.</summary>
public sealed partial class InterruptWireTests : IAsyncLifetime
{
    private readonly AgentHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_run_checks_the_warranty_finds_no_serial_number_and_ends_with_two_interrupts()
    {
        var (frames, _, _) = await Wire.AskAsync(_host.CreateHttpClient(), Ct);

        Assert.Equal("RUN_STARTED", Type(frames[0]));
        Assert.Contains(frames, f => Type(f) == "TOOL_CALL_RESULT" && f.GetProperty("content").GetString()!.Contains("no serial number", StringComparison.Ordinal));

        var finished = frames[^1];
        Assert.Equal("RUN_FINISHED", Type(finished));
        Assert.Equal("interrupt", finished.GetProperty("outcome").GetProperty("type").GetString());
        Assert.Equal(2, finished.GetProperty("outcome").GetProperty("interrupts").GetArrayLength());

        // The model's requests for input never appear as tool calls: only check_warranty does.
        Assert.All(frames.Where(f => Type(f) == "TOOL_CALL_START"), f => Assert.Equal("check_warranty", f.GetProperty("toolCallName").GetString()));
        Assert.All(frames, f => Assert.Empty(AGUIWire.NullProperties(f)));
    }

    [Fact]
    public async Task Each_interrupt_carries_every_field_from_the_server_record()
    {
        var (frames, serialId, readingId) = await Wire.AskAsync(_host.CreateHttpClient(), Ct);
        var interrupts = frames[^1].GetProperty("outcome").GetProperty("interrupts");

        var serial = interrupts[0];
        Assert.Matches(ServerId(), serialId);
        Assert.Matches(ServerId(), readingId);
        Assert.Equal("input_required", serial.GetProperty("reason").GetString());
        Assert.StartsWith("EQ-601, the Contoso Air HP-24S heat pump at Maple Grove Apartments, has no serial number", serial.GetProperty("message").GetString());
        Assert.False(serial.TryGetProperty("toolCallId", out _)); // a question, not an approval

        // Fifteen minutes from the server's clock, to the second, in UTC.
        Assert.Equal("2026-11-16T16:35:00Z", serial.GetProperty("expiresAt").GetString());

        // The schema is inside the subset every Beacon client renders.
        Assert.True(FormSchema.TryParse(serial.GetProperty("responseSchema"), out var form, out _));
        Assert.Equal(["serialNumber", "readFrom", "nameplatePhotoTaken"], form.Fields.Select(f => f.Name));
        Assert.Equal("^HP24S-[0-9]{4}-[0-9]{4}$", form.Field("serialNumber")!.Pattern);

        // Metadata is context under Beacon's own key; the reading carries the motor's rating.
        Assert.Equal("serial-number", serial.GetProperty("metadata").GetProperty("beacon").GetProperty("form").GetString());
        Assert.Equal(1.4m, interrupts[1].GetProperty("metadata").GetProperty("beacon").GetProperty("ratedAmps").GetDecimal());
    }

    [Fact]
    public async Task The_pipeline_content_serializes_only_its_ID_into_rawEvent()
    {
        var (frames, serialId, _) = await Wire.AskAsync(_host.CreateHttpClient(), Ct);

        // AGUI.Server attaches the update that closed the text message, which is the one carrying the request.
        var end = frames.Last(f => Type(f) == "TEXT_MESSAGE_END");
        var content = end.GetProperty("rawEvent").GetProperty("contents")[0];

        Assert.Equal("beaconInputRequested", content.GetProperty("$type").GetString());
        Assert.Equal(serialId, content.GetProperty("inputId").GetString());
        Assert.False(content.TryGetProperty("request", out _));
    }

    [Fact]
    public async Task Nothing_is_asked_when_the_answer_is_on_record()
    {
        // EQ-501 has a serial number, so ask_for_serial_number runs as an ordinary tool and answers itself.
        var frames = await AGUIWire.RunRawAsync(_host.CreateHttpClient(), "/agent",
            AGUIWire.UserRun("What's the serial number of EQ-501?", "t-501"), Ct);

        Assert.Equal("success", frames[^1].GetProperty("outcome").GetProperty("type").GetString());
        var result = frames.Single(f => Type(f) == "TOOL_CALL_RESULT").GetProperty("content").GetString()!;
        Assert.Contains("CA36X-2219-0441", result, StringComparison.Ordinal);
    }

    private static string Type(JsonElement frame) => frame.GetProperty("type").GetString()!;

    [GeneratedRegex("^inp_[0-9a-f]{32}$")]
    private static partial Regex ServerId();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
