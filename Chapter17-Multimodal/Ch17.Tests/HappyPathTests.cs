using AGUI.Abstractions;
using Beacon.Domain;
using Beacon.Testing;
using Ch17.Beacon.Agent;
using Microsoft.Extensions.DependencyInjection;

namespace Ch17.Tests;

/// <summary>Each kind of media, end to end through the agent and the scripted model, over the real SSE stream.</summary>
public sealed class HappyPathTests
{
    [Fact]
    public async Task A_nameplate_photo_identifies_the_unit_and_Beacon_offers_to_record_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = new AgentHost();

        var frames = await AGUIWire.RunRawAsync(agent.CreateClient(), "/agent",
            Runs.User(Runs.Text("Here's the nameplate on the WO-1003 heat pump."), Runs.Image(Samples.Nameplate, filename: "eq-601-nameplate.png")), ct);

        Assert.Equal(["RUN_STARTED", "TEXT_MESSAGE_START"], frames.Take(2).Select(Sse.TypeOf));
        Assert.DoesNotContain(frames, f => Sse.TypeOf(f) == "CUSTOM");
        Assert.Equal(MediaScripts.NameplateReading, Sse.Text(frames));
    }

    [Fact]
    public async Task Confirming_the_reading_records_the_serial_number()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = new AgentHost();

        var input = new RunAgentInput
        {
            ThreadId = "thread-17",
            RunId = "run-2",
            Messages =
            [
                new AGUIUserMessage { Id = "user-1", Content = new AGUIContent([Runs.Text("Here's the nameplate on the WO-1003 heat pump."), Runs.Image(Samples.Nameplate)]) },
                new AGUIAssistantMessage { Id = "msg-1", Content = MediaScripts.NameplateReading },
                new AGUIUserMessage { Id = "user-2", Content = "Yes, record it." },
            ],
        };
        var frames = await AGUIWire.RunRawAsync(agent.CreateClient(), "/agent", input, ct);

        var start = frames.Single(f => Sse.TypeOf(f) == "TOOL_CALL_START");
        Assert.Equal("record_serial_number", start.GetProperty("toolCallName").GetString());
        var result = frames.Single(f => Sse.TypeOf(f) == "TOOL_CALL_RESULT").GetProperty("content").GetString();
        Assert.Equal("""{"equipmentId":"EQ-601","serialNumber":"HP24S-2207-1184","recorded":true}""", result);
        Assert.Equal("HP24S-2207-1184", agent.Services.GetRequiredService<IFieldServiceStore>().GetEquipment("EQ-601")!.SerialNumber);
    }

    [Theory]
    [InlineData("HP24S-2207-1184\n")]   // $ would match before a final newline; \z doesn't
    [InlineData("HP24S-22O7-1184")]     // Chapter 15's letter O
    [InlineData("HP24S-2207-1184-9")]
    public void The_tool_refuses_a_reading_that_is_not_exactly_the_printed_shape(string reading)
    {
        var store = new InMemoryFieldServiceStore();

        var result = new EquipmentTools(store).RecordSerialNumber("EQ-601", reading);

        Assert.IsType<global::Beacon.AI.ToolError>(result);
        Assert.Null(store.GetEquipment("EQ-601")!.SerialNumber);
    }

    [Fact]
    public async Task A_recording_feeds_the_diagnosis_and_a_stock_check()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = new AgentHost();

        var frames = await AGUIWire.RunRawAsync(agent.CreateClient(), "/agent",
            Runs.User(Runs.Text("This is the noise from the outdoor unit on WO-1003."), Runs.Audio(Samples.FanRecording)), ct);

        Assert.Contains(frames, f => Sse.TypeOf(f) == "REASONING_MESSAGE_CONTENT");
        Assert.StartsWith("I can hear a rasping grind once per fan revolution", Sse.Text(frames), StringComparison.Ordinal);
        Assert.Equal("search_parts", frames.Single(f => Sse.TypeOf(f) == "TOOL_CALL_START").GetProperty("toolCallName").GetString());
        Assert.Contains("BRG-KIT-1", Sse.Text(frames), StringComparison.Ordinal);
        Assert.Contains("FAN-HP24", Sse.Text(frames), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_PDF_bulletin_is_read_and_applied_to_the_job()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = new AgentHost();

        var frames = await AGUIWire.RunRawAsync(agent.CreateClient(), "/agent",
            Runs.User(Runs.Text("Does this bulletin apply to WO-1003?"), Runs.Document(Samples.Bulletin)), ct);

        Assert.StartsWith("That's Contoso Air service bulletin SB-HP24S-07, and it fits WO-1003.", Sse.Text(frames), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_image_the_scripted_model_does_not_know_gets_an_honest_answer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = new AgentHost();

        var other = Samples.PngOfSize(2048);
        var frames = await AGUIWire.RunRawAsync(agent.CreateClient(), "/agent", Runs.User(Runs.Image(other)), ct);

        Assert.StartsWith("Thanks, the photo arrived (2 KB). I'm Beacon's scripted stand-in", Sse.Text(frames), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_frame_on_the_wire_is_null_or_echoes_the_media()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = new AgentHost();

        var frames = await AGUIWire.RunRawAsync(agent.CreateClient(), "/agent",
            Runs.User(Runs.Text("Here's the nameplate on the WO-1003 heat pump."), Runs.Image(Samples.Nameplate)), ct);

        Assert.All(frames, f => Assert.Empty(AGUIWire.NullProperties(f)));
        var base64Start = Convert.ToBase64String(Samples.Nameplate)[..64];
        Assert.DoesNotContain(frames, f => f.GetRawText().Contains(base64Start, StringComparison.Ordinal)); // RUN_STARTED doesn't echo the input
    }
}
