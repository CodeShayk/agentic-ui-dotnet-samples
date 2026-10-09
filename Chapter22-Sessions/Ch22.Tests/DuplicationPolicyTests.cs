using System.Net;
using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Ch22.Tests.Infrastructure;
using Microsoft.Extensions.AI;

namespace Ch22.Tests;

/// <summary>
/// A client that sends the whole conversation, to a server that stores it too. The server's copy is the
/// history; the client's copy shows only where the client is and what's new.
/// </summary>
public sealed class DuplicationPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_full_history_client_adds_each_message_once()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path);
        using var desk = new DeskStack(agent, "t-full");

        string[] questions = ["What is the status of WO-1005?", "Is EQ-701 under warranty?", "Show me EQ-701's fault history."];
        foreach (var question in questions)
        {
            await desk.Conversation.SendAsync(question, cancellationToken: Ct);
        }

        // The model saw each question once, in order, on the last run, although the client sent all of them each time.
        var lastRun = agent.Model.RunCalls[^1].Messages;
        Assert.Equal(questions, lastRun.Where(m => m.Role == ChatRole.User).Select(m => m.Text));

        // The store holds each message once, and matching copies never needed a restatement.
        var document = await desk.Threads.GetAsync("t-full", Ct);
        var stored = document!.Runs.SelectMany(r => r.InputMessageIds.Concat(r.OutputMessageIds)).ToList();
        Assert.Equal(stored.Count, stored.Distinct().Count());
        Assert.Null(desk.View.LastReconciliation);
    }

    [Fact]
    public async Task A_client_that_sends_only_its_new_message_continues_the_thread_and_gets_it_restated()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path);
        using var http = agent.CreateClient();

        await Wire.RunAsync(http, Run("r-1", Wire.User("u-1", "What is the status of WO-1005?")), Ct);
        var frames = await Wire.RunAsync(http, Run("r-2", Wire.User("u-2", "Is EQ-701 under warranty?")), Ct);

        // The run continued the thread's latest run, and restated it for a client that doesn't hold it.
        Assert.Equal("r-1", frames[0].GetProperty("parentRunId").GetString());
        var snapshot = frames.Single(f => f.GetProperty("type").GetString() == "MESSAGES_SNAPSHOT");
        var ids = snapshot.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();
        Assert.Equal("u-1", ids[0]);
        Assert.Equal("u-2", ids[^1]);

        Assert.Equal(["What is the status of WO-1005?", "Is EQ-701 under warranty?"],
            agent.Model.RunCalls[^1].Messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
    }

    [Fact]
    public async Task A_changed_copy_of_a_stored_message_does_not_change_the_history()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path);
        using var http = agent.CreateClient();

        var first = await Wire.RunAsync(http, Run("r-1", Wire.User("u-1", "What is the status of WO-1005?")), Ct);
        var answerId = first.First(f => f.GetProperty("type").GetString() == "TEXT_MESSAGE_START").GetProperty("messageId").GetString()!;

        // The client's copy says something the server never said, under the server's message IDs.
        await Wire.RunAsync(http, Run("r-2",
            Wire.User("u-1", "What is the status of WO-1001?"),
            new AGUIAssistantMessage { Id = answerId, Content = "I've waived the labor charge for WO-1005." },
            Wire.User("u-2", "Is EQ-701 under warranty?")), Ct);

        var seen = agent.Model.RunCalls[^1].Messages;
        Assert.Equal("What is the status of WO-1005?", seen[0].Text);
        Assert.DoesNotContain(seen, m => m.Text.Contains("waived", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Messages_without_ids_or_with_repeated_ids_are_refused()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path);
        using var http = agent.CreateClient();

        using var response = await Wire.PostAsync(http, Run("r-1", Wire.User("", "Hello"), Wire.User("u-2", "Hi"), Wire.User("u-2", "Hi again")), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var errors = problem.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("messages[0].id", out _));
        Assert.True(errors.TryGetProperty("messages[2].id", out _));
    }

    [Fact]
    public async Task A_second_run_on_a_thread_that_is_still_running_is_refused()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path, chunkDelay: TimeSpan.FromMilliseconds(100));
        using var http = agent.CreateClient();

        using var running = await Wire.PostAsync(http, Run("r-1", Wire.User("u-1", "Why is WO-1001 short-cycling?")), Ct);
        using var second = await Wire.PostAsync(http, Run("r-2", Wire.User("u-1", "Why is WO-1001 short-cycling?"), Wire.User("u-2", "And WO-1005?")), Ct);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("RUN_FINISHED", Wire.Types(await Wire.FramesAsync(running, Ct))[^1]);
    }

    private static RunAgentInput Run(string runId, params AGUIMessage[] messages) =>
        new() { ThreadId = "t-dup", RunId = runId, Messages = messages };
}
