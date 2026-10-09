using Beacon.Testing;
using Ch26.Beacon.Agent.Recording;
using Ch26.Tests.Integration.Infrastructure;
using Microsoft.Extensions.AI;

namespace Ch26.Tests.Integration.Replay;

/// <summary>
/// Section 26.2: a real model's output, recorded once, replayed forever. The recording replaces only the
/// model; the agent's tools, gates and converter run for real on every replay.
/// </summary>
public sealed class RecordedModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_recorded_llama_run_replays_the_same_way_every_time()
    {
        var input = Wire.UserRun("thread-llama", "llama-warranty-eq501", "Is EQ-501 still under warranty?");

        List<string> answers = [];
        for (var i = 0; i < 3; i++)
        {
            // Load the recording for every replay: FunctionInvokingChatClient marks a call it has handled
            // on the FunctionCallContent itself, so replaying the same objects twice skips the tool.
            var recording = await Recordings.LoadAsync(Recordings.LlamaWarranty1);
            Assert.Equal("llama3.2", recording.Model);

            await using var agent = new AgentHost(recording.ToTurns());
            var frames = await Wire.PostAsync(agent.CreateClient(), input, Ct);
            answers.Add(Wire.Text(frames));

            // The model asked for check_warranty; the real tool answered from Beacon's data.
            var result = Assert.Single(frames.OfType("TOOL_CALL_RESULT"));
            Assert.Contains("2029-05-14", result.GetProperty("content").GetString());
            Assert.Equal(2, agent.Recorded!.Calls.Count); // the call, then the answer
        }

        Assert.Single(answers.Distinct());
        Assert.Equal(
            "EQ-501 is still under warranty, specifically for parts for 10 years. Labor coverage ended 2 years ago.",
            answers[0]);
    }

    [Fact]
    public async Task The_recorder_saves_what_the_model_said_before_any_layer_changes_it()
    {
        var directory = Directory.CreateTempSubdirectory("ch26-recording-").FullName;
        try
        {
            // The scripted order turn: text, then a place_part_order call the spending gate turns into an approval.
            var original = await Recordings.LoadAsync(Recordings.Order);
            await using var agent = new AgentHost(
                new RecordedChatClient(original.ToTurns()),
                new Dictionary<string, string?> { ["Beacon:Recording:Directory"] = directory });

            var input = Wire.UserRun("thread-capture", "capture-1", "Order a replacement compressor CMP-CA36 for WO-1001.");
            var frames = await Wire.PostAsync(agent.CreateClient(), input, Ct);
            Assert.Equal("interrupt", frames[^1].GetProperty("outcome").GetProperty("type").GetString());

            var saved = await ModelRecording.LoadAsync(Path.Combine(directory, "capture-1.model.json"), Ct);
            var turn = Assert.Single(saved.Turns);

            // The file holds the model's call, not the approval request the gate made of it.
            var call = Assert.IsType<FunctionCallContent>(Assert.Single(turn[^1].Contents));
            Assert.Equal("place_part_order", call.Name);
            Assert.Equal("CMP-CA36", call.Arguments!["sku"]!.ToString());
            Assert.Equal(
                original.Turns[0].Select(u => u.Text),
                turn.Select(u => u.Text));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_string_quantity_from_a_real_model_still_needs_approval()
    {
        // llama3.2 sent "quantity":"1", a string. The first version of the spending check couldn't price
        // it, treated that as "within the limit" and placed a $1,795 order. This recording keeps it fixed.
        await using var agent = await AgentHost.ReplayingAsync(Recordings.LlamaOrderStringQuantity);
        var frames = await Wire.PostAsync(
            agent.CreateClient(), Wire.UserRun("thread-llama", "llama-order", "Order a replacement compressor CMP-CA36 for WO-1001."), Ct);

        var args = Assert.Single(frames.OfType("TOOL_CALL_ARGS")).GetProperty("delta").GetString();
        Assert.Contains("\"quantity\":\"1\"", args);
        Assert.Empty(frames.OfType("TOOL_CALL_RESULT"));
        var outcome = frames[^1].GetProperty("outcome");
        Assert.Equal("interrupt", outcome.GetProperty("type").GetString());
        Assert.StartsWith("apr_", outcome.GetProperty("interrupts")[0].GetProperty("id").GetString());
        Assert.Single(agent.Recorded!.Calls); // the summary turn in the recording is never reached
    }
}
