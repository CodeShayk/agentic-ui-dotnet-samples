using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Testing;
using Ch19.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch19.Tests;

/// <summary>
/// The one downgrade the agent offers: a client that declares no protocol version gets Chapter 18's
/// attribution without the SUBAGENT_* lifecycle events it couldn't decode, and the loss is logged.
/// </summary>
public sealed class SubagentDowngradeTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // What Chapter 18's coordinator writes for one specialist, as raw events, then the coordinator's own answer.
    private static IReadOnlyList<ChatResponseUpdate> SpecialistTurn() =>
    [
        Raw(new SubagentStartedEvent { SubagentRunId = "parts-1", Name = "parts-specialist" }),
        Raw(new TextMessageStartEvent { MessageId = "m-1", Role = AGUIRoles.Assistant, SubagentRunId = "parts-1" }),
        Raw(new TextMessageContentEvent { MessageId = "m-1", Delta = "CAP-45-5 is in stock.", SubagentRunId = "parts-1" }),
        Raw(new TextMessageEndEvent { MessageId = "m-1", SubagentRunId = "parts-1" }),
        Raw(new SubagentFinishedEvent { SubagentRunId = "parts-1" }),
        new ChatResponseUpdate(ChatRole.Assistant, "The capacitor is in stock.") { MessageId = "m-2" },
    ];

    [Theory]
    [InlineData(null, false)]
    [InlineData("1.0", true)]
    [InlineData("1.4", true)]
    public async Task Only_a_client_that_declares_nothing_loses_the_subagent_lifecycle(string? declared, bool lifecycle)
    {
        await using var host = new AgentHost(model: new RecordedChatClient(SpecialistTurn()));
        var input = AGUIWire.UserRun("Line up the parts for WO-1001.", "t-19", "r-1");
        input.ProtocolVersion = declared;

        var frames = await AGUIWire.RunRawAsync(host.CreateClient(), "/agent", input, Cancel);

        Assert.Equal(lifecycle, frames.Any(f => Type(f).StartsWith("SUBAGENT_", StringComparison.Ordinal)));

        // Attribution stays either way: an older parser ignores the field, so keeping it costs that client nothing.
        var specialist = frames.Where(f => f.TryGetProperty("messageId", out var id) && id.GetString() == "m-1").ToList();
        Assert.Equal(["TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END"], specialist.Select(Type));
        Assert.All(specialist, f => Assert.Equal("parts-1", f.GetProperty("subagentRunId").GetString()));

        // The rest of the run is untouched: same declaration, same outcome.
        Assert.Equal("1.0", frames[0].GetProperty("protocolVersion").GetString());
        Assert.Equal("RUN_FINISHED", Type(frames[^1]));

        // Dropping the lifecycle is lossy, so the developer is told what was left out.
        var warnings = host.Logs.WithEventId(1905).ToList();
        if (lifecycle)
        {
            Assert.Empty(warnings);
        }
        else
        {
            var warning = Assert.Single(warnings);
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Contains("2 subagent lifecycle events (SUBAGENT_STARTED, SUBAGENT_FINISHED, SUBAGENT_ERROR)", warning.Message, StringComparison.Ordinal);
        }
    }

    private static ChatResponseUpdate Raw(BaseEvent evt) => new() { RawRepresentation = evt };

    private static string Type(JsonElement frame) => frame.GetProperty("type").GetString()!;
}
