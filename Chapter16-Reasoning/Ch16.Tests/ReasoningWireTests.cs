using System.Text.Json;
using Beacon.Testing;
using Ch16.Beacon.Agent.Reasoning;
using Ch16.Beacon.Agent.Scripted;
using Ch16.Tests.Infrastructure;

namespace Ch16.Tests;

/// <summary>What each disclosure setting puts on the wire for WO-1001's diagnosis, read from the raw SSE frames.</summary>
public sealed class ReasoningWireTests
{
    [Fact]
    public async Task Reasoning_brackets_the_tool_call_and_closes_before_the_answer_starts()
    {
        await using var host = new AgentHost();
        var frames = await Runs.RawAsync(host.CreateClient(), Runs.Turn("t-1", "r-1", Runs.User("u-1", Runs.Diagnose)));

        Assert.Equal(
        [
            "RUN_STARTED",
            "REASONING_START", "REASONING_MESSAGE_START", "REASONING_MESSAGE_CONTENT", "REASONING_ENCRYPTED_VALUE", "REASONING_MESSAGE_END", "REASONING_END",
            "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "TOOL_CALL_RESULT",
            "REASONING_START", "REASONING_MESSAGE_START", "REASONING_MESSAGE_CONTENT", "REASONING_ENCRYPTED_VALUE", "REASONING_MESSAGE_END", "REASONING_END",
            "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END",
            "RUN_FINISHED",
        ],
        Runs.Shape(frames));

        // Each span and its message share an ID here; the encrypted value names the message it belongs to,
        // which is still open when the value arrives.
        foreach (var encrypted in frames.OfType("REASONING_ENCRYPTED_VALUE"))
        {
            var index = frames.ToList().IndexOf(encrypted);
            var opened = frames.Take(index).OfType("REASONING_MESSAGE_START").Last();
            Assert.Equal("message", encrypted.GetProperty("subtype").GetString());
            Assert.Equal(opened.GetProperty("messageId").GetString(), encrypted.GetProperty("entityId").GetString());
            Assert.Equal("reasoning", opened.GetProperty("role").GetString());
            Assert.StartsWith("bx1.", encrypted.GetProperty("encryptedValue").GetString(), StringComparison.Ordinal);
        }

        // Optional fields are omitted, never null.
        Assert.All(frames, frame => Assert.Empty(AGUIWire.NullProperties(frame)));
    }

    [Fact]
    public async Task Summary_shows_the_summary_and_redacts_the_phone_number_everywhere_on_the_wire()
    {
        await using var host = new AgentHost(ReasoningDisclosure.Summary);
        var frames = await Runs.RawAsync(host.CreateClient(), Runs.Turn("t-1", "r-1", Runs.User("u-1", Runs.Diagnose)));

        var shown = ReasoningTexts(frames);
        Assert.Equal(DiagnosisText.Summary1, shown[0]);
        Assert.Equal(DiagnosisText.Summary2.Replace("555-0141", "[phone number removed]", StringComparison.Ordinal), shown[1]);

        // Not in the reasoning, and not in any rawEvent either: the policy rebuilds the updates it touches.
        Assert.All(frames, frame => Assert.DoesNotContain("555-0141", frame.GetRawText(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Full_shows_the_whole_chain_redacted()
    {
        await using var host = new AgentHost(ReasoningDisclosure.Full);
        var frames = await Runs.RawAsync(host.CreateClient(), Runs.Turn("t-1", "r-1", Runs.User("u-1", Runs.Diagnose)));

        var shown = ReasoningTexts(frames);
        Assert.Equal(DiagnosisText.Chain1, shown[0]);
        Assert.Equal(DiagnosisText.Chain2.Replace("555-0141", "[phone number removed]", StringComparison.Ordinal), shown[1]);
        Assert.Equal("Full", host.Model.Options[0]!.Reasoning!.Output.ToString());
    }

    [Fact]
    public async Task Summary_caps_each_reasoning_message_at_the_configured_length()
    {
        await using var host = new AgentHost(ReasoningDisclosure.Summary, maxShownCharacters: 80);
        var frames = await Runs.RawAsync(host.CreateClient(), Runs.Turn("t-1", "r-1", Runs.User("u-1", Runs.Diagnose)));

        var shown = ReasoningTexts(frames);
        Assert.All(shown, text => Assert.True(text.Length <= 80, $"{text.Length} characters: {text}"));
        Assert.Equal("Short-cycling with warm air points to the compressor tripping on overload.", shown[0].TrimEnd());
        Assert.EndsWith("…", shown[1], StringComparison.Ordinal); // a first sentence longer than the budget is cut
    }

    [Fact]
    public async Task Hidden_sends_no_reasoning_text_but_every_sealed_value_still_has_a_message_to_belong_to()
    {
        await using var host = new AgentHost(ReasoningDisclosure.Hidden);
        var frames = (await Runs.RawAsync(host.CreateClient(), Runs.Turn("t-1", "r-1", Runs.User("u-1", Runs.Diagnose)))).ToList();

        Assert.Empty(frames.OfType("REASONING_MESSAGE_CONTENT"));
        Assert.Equal("None", host.Model.Options[0]!.Reasoning!.Output.ToString());

        var encrypted = frames.OfType("REASONING_ENCRYPTED_VALUE").ToList();
        Assert.Equal(2, encrypted.Count);
        foreach (var value in encrypted)
        {
            var id = value.GetProperty("entityId").GetString();
            var start = frames.FindIndex(f => Runs.Type(f) == "REASONING_MESSAGE_START" && f.GetProperty("messageId").GetString() == id);
            var end = frames.FindIndex(f => Runs.Type(f) == "REASONING_MESSAGE_END" && f.GetProperty("messageId").GetString() == id);
            Assert.InRange(frames.IndexOf(value), start + 1, end - 1);
        }

        // What the provider spent on thinking still shows, as a count.
        var usage = Assert.Single(frames.OfType("RUN_FINISHED")).GetProperty("usage")[0];
        Assert.True(usage.GetProperty("reasoningTokens").GetInt64() > 0);
        Assert.True(usage.GetProperty("outputTokens").GetInt64() > usage.GetProperty("reasoningTokens").GetInt64());
    }

    // The concatenated deltas of each reasoning message, in order.
    private static List<string> ReasoningTexts(IEnumerable<JsonElement> frames) =>
    [
        .. frames.OfType("REASONING_MESSAGE_CONTENT")
            .GroupBy(f => f.GetProperty("messageId").GetString())
            .Select(g => string.Concat(g.Select(f => f.GetProperty("delta").GetString()))),
    ];
}
