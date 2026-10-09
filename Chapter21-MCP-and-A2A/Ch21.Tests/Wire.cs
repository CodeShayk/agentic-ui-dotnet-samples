using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Testing;

namespace Ch21.Tests;

/// <summary>Helpers for reading and judging what Beacon put on the AG-UI stream.</summary>
internal static class Wire
{
    public const string CombinedQuestion =
        "For WO-1005, find the locked rotor procedure in the RT-120 manual and ask Northwind whether they can ship a CMP-RT120.";

    public const string CapacitorQuestion = "What does the CA-36X manual say about testing the run capacitor?";

    /// <summary>Chapter 3's ordering rules, plus the subagent rules this chapter's stream must keep.</summary>
    public static void AssertWellFormed(IReadOnlyList<BaseEvent> events)
    {
        Assert.IsType<RunStartedEvent>(events[0]);
        Assert.IsType<RunFinishedEvent>(events[^1]);
        Assert.Single(events, e => e is RunFinishedEvent or RunErrorEvent);

        HashSet<string> openMessages = [];
        HashSet<string> openToolCalls = [];
        HashSet<string> endedToolCalls = [];
        HashSet<string> activeSubagents = [];
        foreach (var evt in events)
        {
            switch (evt)
            {
                case TextMessageStartEvent e:
                    Assert.True(openMessages.Add(e.MessageId));
                    break;
                case TextMessageContentEvent e:
                    Assert.Contains(e.MessageId, openMessages);
                    break;
                case TextMessageEndEvent e:
                    Assert.True(openMessages.Remove(e.MessageId));
                    break;
                case ToolCallStartEvent e:
                    Assert.True(openToolCalls.Add(e.ToolCallId));
                    break;
                case ToolCallEndEvent e:
                    Assert.True(openToolCalls.Remove(e.ToolCallId));
                    endedToolCalls.Add(e.ToolCallId);
                    break;
                case ToolCallResultEvent e:
                    Assert.Contains(e.ToolCallId, endedToolCalls);
                    break;
                case SubagentStartedEvent e:
                    Assert.True(activeSubagents.Add(e.SubagentRunId!), $"Subagent {e.SubagentRunId} announced twice.");
                    Assert.Contains(e.ParentToolCallId!, endedToolCalls);
                    break;
                case SubagentFinishedEvent e:
                    Assert.True(activeSubagents.Remove(e.SubagentRunId!), $"Subagent {e.SubagentRunId} closed but not active.");
                    break;
                case SubagentErrorEvent e:
                    Assert.True(activeSubagents.Remove(e.SubagentRunId!), $"Subagent {e.SubagentRunId} failed but not active.");
                    break;
            }
        }

        Assert.Empty(openMessages);
        Assert.Empty(openToolCalls);
        Assert.Empty(activeSubagents);
    }

    /// <summary>The names of the tools the run called, in order.</summary>
    public static IReadOnlyList<string> ToolCalls(IEnumerable<BaseEvent> events) =>
        [.. events.OfType<ToolCallStartEvent>().Select(e => e.ToolCallName)];

    /// <summary>The result of the first call to a tool, parsed: what the model was handed.</summary>
    public static JsonElement Result(IReadOnlyList<BaseEvent> events, string toolName)
    {
        var call = events.OfType<ToolCallStartEvent>().First(e => e.ToolCallName == toolName);
        var result = events.OfType<ToolCallResultEvent>().Single(e => e.ToolCallId == call.ToolCallId);
        var content = AGUIWire.ToWireJson(result).GetProperty("content").GetString()!;
        using var document = JsonDocument.Parse(content);
        return document.RootElement.Clone();
    }

    /// <summary>The text the run's messages carried, concatenated.</summary>
    public static string Text(IEnumerable<BaseEvent> events) =>
        string.Concat(events.OfType<TextMessageContentEvent>().Select(e => e.Delta));
}
