using AGUI.Abstractions;
using Beacon.Client.Core;
using Ch22.Beacon.Agent.Compaction;
using Ch22.Beacon.Desk.Sessions;
using Ch22.Tests.Infrastructure;
using Microsoft.Extensions.AI;

namespace Ch22.Tests;

/// <summary>Compaction: the model sees a summary of old turns; nothing it needs to finish a turn is ever summarized.</summary>
public sealed class CompactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_cut_starts_a_turn_and_never_separates_a_call_from_its_result()
    {
        // A frontend call left unanswered when the user typed again: its result arrives after the next question.
        AGUIMessage[] messages =
        [
            User("u-1"), Calls("a-1", "c-1"), Result("c-1"), Text("a-1b"),
            User("u-2"), Calls("a-2", "c-2"),
            User("u-3"), Result("c-2"), Text("a-3"),
            User("u-4"), Text("a-4"),
        ];

        // Keeping the last two turns would cut before u-3, between c-2 and its result. The cut moves back to u-2.
        Assert.False(HistoryCompactor.KeepsToolPairs(messages, 6));
        Assert.Equal(4, HistoryCompactor.FindCut(messages, limit: messages.Length, keepRecentTurns: 2));

        // A cut never lands on anything but a user message, and none is left before the first.
        Assert.Equal(0, HistoryCompactor.FindCut(messages[..4], limit: 4, keepRecentTurns: 0));
    }

    [Fact]
    public async Task A_long_conversation_is_compacted_without_splitting_tool_pairs_or_the_open_question()
    {
        using var folder = new TempFolder();

        // A budget the first answer alone nearly fills, and no recent turns kept by policy: only the turn rule and
        // the interrupt guard stand between the open question and the summary.
        await using var agent = new AgentHost(folder.Path, AgentHost.TightBudget(tokens: 290, keepRecentTurns: 0));
        using var desk = new DeskStack(agent, "t-long");

        await desk.Conversation.SendAsync("What is the status of WO-1005?", cancellationToken: Ct);
        await desk.Conversation.SendAsync("Is EQ-701 under warranty?", cancellationToken: Ct);
        await Elena.AskAsync(desk);
        await Elena.AnswerAsync(desk);

        // Two compactions: one before the second question, one when the readings came back.
        Assert.Equal(2, agent.Model.SummaryCalls.Count);
        Assert.Contains("4.3% unbalanced", desk.View.Messages.OfType<AssistantMessageView>().Last().Text, StringComparison.Ordinal);

        foreach (var (messages, options) in agent.Model.RunCalls)
        {
            // A summary, when there is one, comes first as labeled data, once, and never in the instructions.
            // The history after it starts at a turn and pairs every call with its result.
            Assert.InRange(messages.Count(IsSummary), 0, 1);
            Assert.DoesNotContain("- Asked:", options!.Instructions, StringComparison.Ordinal);
            Assert.Equal(ChatRole.User, messages.SkipWhile(IsSummary).First().Role);
            var calls = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToHashSet();
            Assert.All(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>(), r => Assert.Contains(r.CallId, calls));
        }

        // The run that answered the question saw a summary of what came before, then the turn that asked it in full.
        var resumed = agent.Model.RunCalls.Single(c => c.Messages[^1].Contents.OfType<FunctionResultContent>().Any(r => r.Result?.ToString()?.Contains("l1l2", StringComparison.Ordinal) == true));
        Assert.True(IsSummary(resumed.Messages[0]));
        Assert.Contains("- Asked: Is EQ-701 under warranty?", resumed.Messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(Elena.C33Question, resumed.Messages[1].Text);
        Assert.Contains(resumed.Messages, m => m.Text.StartsWith("EQ-701 logged", StringComparison.Ordinal));
        Assert.EndsWith(HistoryCompactor.SummaryRule, resumed.Options!.Instructions, StringComparison.Ordinal);

        // One more turn summarizes the readings too. The model still knows them, from the summary alone.
        await Elena.AskForNoteAsync(desk);
        Assert.Equal(3, agent.Model.SummaryCalls.Count);
        Assert.DoesNotContain(agent.Model.RunCalls[^1].Messages, m => m.Contents.OfType<FunctionResultContent>().Any());
        Assert.Contains("4.3% unbalanced", desk.View.Messages.OfType<AssistantMessageView>().Last().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_transcript_keeps_every_message_and_the_restored_view_shows_the_compaction()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path, AgentHost.TightBudget(tokens: 300, keepRecentTurns: 1));
        using var desk = new DeskStack(agent, "t-long");
        await desk.Conversation.SendAsync("What is the status of WO-1005?", cancellationToken: Ct);
        await desk.Conversation.SendAsync("Is EQ-701 under warranty?", cancellationToken: Ct);
        await desk.Conversation.SendAsync("Show me EQ-701's fault history.", cancellationToken: Ct);

        var restored = ConversationReplay.Fold("t-long", await desk.Threads.ReadEventsAsync("t-long", null, Ct));

        Assert.Equal(3, restored.Messages.OfType<UserMessageView>().Count());
        Assert.Equal(Rendered.Of(desk.View), Rendered.Of(restored));
        Assert.NotNull(restored.CustomEvents.LastOrDefault(e => e.Name == "beacon.compaction"));
        Assert.True((await desk.Threads.ListAsync("WO-1005", Ct)).Single().Compacted);
    }

    private static bool IsSummary(ChatMessage message) =>
        message.Role == ChatRole.User && message.Text.StartsWith(HistoryCompactor.SummaryLabel, StringComparison.Ordinal);

    private static AGUIUserMessage User(string id) => new() { Id = id, Content = id };

    private static AGUIAssistantMessage Text(string id) => new() { Id = id, Content = id };

    private static AGUIAssistantMessage Calls(string id, string callId) => new()
    {
        Id = id,
        ToolCalls = [new AGUIToolCall { Id = callId, Type = "function", Function = new AGUIToolCallFunction { Name = "show_work_order", Arguments = "{}" } }],
    };

    private static AGUIToolMessage Result(string callId) => new() { Id = callId, ToolCallId = callId, Content = "{}" };
}
