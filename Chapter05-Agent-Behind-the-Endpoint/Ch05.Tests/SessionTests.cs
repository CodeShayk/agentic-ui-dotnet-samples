using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Testing;

namespace Ch05.Tests;

/// <summary>
/// Agent Framework sessions keyed by the AG-UI thread ID. With a session store the server owns the
/// history, so the client's contract changes: send only the new messages.
/// </summary>
public sealed class SessionTests
{
    private const string FirstQuestion = "What is the status of WO-1001?";
    private const string FollowUp = "Who is assigned to it?";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_a_session_store_each_run_sees_only_what_the_client_sends()
    {
        await using var ollama = await FakeOllama.StartAsync(cancellationToken: Ct);
        await using var host = AgentHosts.Start(HostKind.AgentFramework, AgentHosts.Ollama(ollama.Endpoint));
        var http = host.CreateClient();

        await AGUIWire.RunAsync(http, "/agent", AGUIWire.UserRun(FirstQuestion, "thread-s", "run-1"), Ct);
        await AGUIWire.RunAsync(http, "/agent", Turn("run-2", new AGUIUserMessage { Id = "user-2", Content = FollowUp }), Ct);

        // Stateless: the server remembered nothing, so the follow-up arrived on its own.
        var sent = FakeOllama.Messages(ollama.Requests[^1]);
        Assert.Equal([("system", BeaconInstructions.Default), ("user", FollowUp)], sent);
    }

    [Fact]
    public async Task With_a_session_store_the_server_keeps_the_thread_and_the_client_sends_only_new_messages()
    {
        await using var ollama = await FakeOllama.StartAsync(cancellationToken: Ct);
        await using var host = AgentHosts.Start(
            HostKind.AgentFramework, AgentHosts.Ollama(ollama.Endpoint), ("Beacon:PersistSessions", "true"));
        var http = host.CreateClient();

        await AGUIWire.RunAsync(http, "/agent", AGUIWire.UserRun(FirstQuestion, "thread-s", "run-1"), Ct);
        await AGUIWire.RunAsync(http, "/agent", Turn("run-2", new AGUIUserMessage { Id = "user-2", Content = FollowUp }), Ct);

        // The session supplied turn one (question, tool call, tool result, answer) ahead of the follow-up.
        var sent = FakeOllama.Messages(ollama.Requests[^1]);
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "user"], sent.Select(m => m.Role));
        Assert.Equal(FirstQuestion, sent[1].Content);
        Assert.Equal(FollowUp, sent[^1].Content);
    }

    [Fact]
    public async Task Sending_the_full_history_to_a_session_duplicates_the_conversation()
    {
        await using var ollama = await FakeOllama.StartAsync(cancellationToken: Ct);
        await using var host = AgentHosts.Start(
            HostKind.AgentFramework, AgentHosts.Ollama(ollama.Endpoint), ("Beacon:PersistSessions", "true"));
        var http = host.CreateClient();

        var first = await AGUIWire.RunAsync(http, "/agent", AGUIWire.UserRun(FirstQuestion, "thread-s", "run-1"), Ct);

        // A stateless client (Chapter 4's console, for one) resends everything.
        await AGUIWire.RunAsync(http, "/agent", Turn(
            "run-2",
            new AGUIUserMessage { Id = "user-1", Content = FirstQuestion },
            new AGUIAssistantMessage { Id = "assistant-1", Content = Wire.Text(first) },
            new AGUIUserMessage { Id = "user-2", Content = FollowUp }), Ct);

        // The store prepends what it kept to what the client sent: turn one now appears twice.
        var sent = FakeOllama.Messages(ollama.Requests[^1]);
        Assert.Equal(2, sent.Count(m => m == ("user", FirstQuestion)));
    }

    private static RunAgentInput Turn(string runId, params AGUIMessage[] messages) => new()
    {
        ThreadId = "thread-s",
        RunId = runId,
        Messages = [.. messages],
    };
}
