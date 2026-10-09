using AGUI.Client;
using Beacon.Client.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ch07.Tests;

/// <summary>
/// The whole stack in one process: Ch07.Beacon.Agent behind WebApplicationFactory, and the client
/// stack Beacon Desk uses in front of it. Three turns, each ending with the agent restating the
/// conversation; if the agent and the reducer disagreed about a single ID, a reconciliation would
/// add or drop a message.
/// </summary>
public sealed class AgentEndToEndTests
{
    [Fact]
    public async Task Three_turns_against_the_real_agent_reconcile_without_changes()
    {
        await using var agent = new WebApplicationFactory<Ch07.Beacon.Agent.Program>().WithWebHostBuilder(host => host
            .UseSetting("AI:ScriptedChunkDelayMilliseconds", "0")
            .UseSetting("Beacon:ToolDelayMilliseconds", "0"));

        var tap = WireEventTap.ForHttp(agent.CreateClient(), "/agent");
        using var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = tap });
        using var conversation = new AgentConversation(client, new ConversationOptions { Tap = tap });

        List<ReconciliationSummary?> summaries = [];
        foreach (var question in new[] { "What is the status of WO-1001?", "Is EQ-501 under warranty?", "Why is it short-cycling?" })
        {
            await conversation.SendAsync(question, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(ConversationStatus.Idle, conversation.View.Status);
            summaries.Add(conversation.View.LastReconciliation);
        }

        Assert.All(summaries, s => Assert.Equal((0, 0, 0), (s!.Changed, s.Added, s.Dropped)));

        var view = conversation.View;
        Assert.Equal(["get_work_order", "check_warranty"], view.ToolCalls.Select(c => c.Name));
        Assert.All(view.ToolCalls, c => Assert.Equal((ToolCallStatus.Completed, ToolExecutor.Agent), (c.Status, c.Executor)));
        Assert.Equal(3, view.Runs.Count);
        Assert.All(view.Runs, r => Assert.Equal(view.ThreadId, r.ThreadId));
        Assert.Single(view.Messages.OfType<ReasoningMessageView>()); // kept: the agent's snapshot doesn't track reasoning
        Assert.Equal(3, view.Messages.OfType<UserMessageView>().Count());
    }
}
