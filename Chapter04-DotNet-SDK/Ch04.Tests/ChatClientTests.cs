using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Server;
using Beacon.AI;
using Beacon.Testing;
using Ch04.Beacon.Console;
using Microsoft.Extensions.AI;

namespace Ch04.Tests;

/// <summary>AGUIChatClient against the in-memory agent: streaming, IDs and thread continuity.</summary>
public sealed class ChatClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_console_session_streams_the_answer_through_AGUIChatClient()
    {
        await using var agent = new AgentFactory(TestModels.Scripted());
        using IChatClient client = new AGUIChatClient(new AGUIChatClientOptions(agent.CreateClient(), "/agent"));
        var session = new BeaconChatSession(client);

        var streamed = new StringBuilder();
        var turn = await session.SendAsync("Hello", text => streamed.Append(text), Ct);

        Assert.Equal(BeaconScripts.Fallback.Text, turn.Text);
        Assert.Equal(turn.Text, streamed.ToString());
        Assert.False(string.IsNullOrEmpty(turn.ThreadId));
        Assert.False(string.IsNullOrEmpty(turn.RunId));
        Assert.Null(turn.ConversationId);
        Assert.Null(turn.Error);
        Assert.Collection(
            session.History,
            message => Assert.Equal(ChatRole.User, message.Role),
            message => Assert.Equal(ChatRole.Assistant, message.Role));
    }

    [Fact]
    public async Task Run_ids_ride_on_RUN_STARTED_and_ConversationId_stays_null()
    {
        await using var agent = new AgentFactory(TestModels.Scripted());
        using IChatClient client = new AGUIChatClient(new AGUIChatClientOptions(agent.CreateClient(), "/agent"));

        List<ChatResponseUpdate> updates = [];
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hello")], new ChatOptions(), Ct))
        {
            updates.Add(update);
        }

        // RUN_STARTED arrives as a content-less update carrying the event itself.
        var startedUpdate = Assert.Single(updates, u => u.RawRepresentation is RunStartedEvent);
        var started = (RunStartedEvent)startedUpdate.RawRepresentation!;
        Assert.Empty(startedUpdate.Contents);
        Assert.Equal(started.ThreadId, startedUpdate.AdditionalProperties?["agui_thread_id"] as string);

        // Text updates are correlated by ResponseId, which is the AG-UI run ID.
        Assert.All(updates.Where(u => u.Text.Length > 0), u => Assert.Equal(started.RunId, u.ResponseId));
        Assert.All(updates, u => Assert.Null(u.ConversationId));
    }

    [Fact]
    public async Task Reusing_one_ChatOptions_keeps_the_thread_and_resends_the_history()
    {
        var model = new RecordedChatClient(
            RecordedChatClient.TextTurn("msg-1", "I'm Beacon. ", "How can I help?"),
            RecordedChatClient.TextTurn("msg-2", "I can't look ", "work orders up yet."));
        await using var agent = new AgentFactory(model);
        using IChatClient client = new AGUIChatClient(new AGUIChatClientOptions(agent.CreateClient(), "/agent"));
        var session = new BeaconChatSession(client);

        var first = await session.SendAsync("Hello", cancellationToken: Ct);
        var second = await session.SendAsync("What is the status of WO-1001?", cancellationToken: Ct);

        Assert.Equal(first.ThreadId, second.ThreadId);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal((1, 3), (first.MessagesSent, second.MessagesSent));

        // The server keeps nothing between runs: the second run carries the whole conversation.
        Assert.True(model.Calls[1].Options!.TryGetRunAgentInput(out var input));
        Assert.Equal(first.ThreadId, input.ThreadId);
        Assert.Equal("1.0", input.ProtocolVersion);
        Assert.Equal(["user", "assistant", "user"], input.Messages.Select(m => m.Role));
        Assert.Equal("msg-1", input.Messages[1].Id);
        Assert.Equal("I'm Beacon. How can I help?", Assert.IsType<AGUIAssistantMessage>(input.Messages[1]).Content);
        Assert.All(input.Messages, m => Assert.False(string.IsNullOrEmpty(m.Id)));
    }

    [Fact]
    public async Task A_new_ChatOptions_for_each_turn_starts_a_new_thread()
    {
        var model = new RecordedChatClient(
            RecordedChatClient.TextTurn("msg-1", "First."),
            RecordedChatClient.TextTurn("msg-2", "Second."));
        await using var agent = new AgentFactory(model);
        using IChatClient client = new AGUIChatClient(new AGUIChatClientOptions(agent.CreateClient(), "/agent"));
        List<ChatMessage> history = [new(ChatRole.User, "Hello")];

        var first = await RunStartedAsync(client, history, new ChatOptions());
        var second = await RunStartedAsync(client, history, new ChatOptions());

        Assert.NotEqual(first.ThreadId, second.ThreadId);
    }

    [Fact]
    public async Task RawRepresentationFactory_sets_the_thread_and_the_parent_run()
    {
        var model = new RecordedChatClient(RecordedChatClient.TextTurn("msg-1", "Trying again."));
        await using var agent = new AgentFactory(model);
        using IChatClient client = new AGUIChatClient(new AGUIChatClientOptions(agent.CreateClient(), "/agent"));

        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => new RunAgentInput { ThreadId = "wo-1001-thread", ParentRunId = "run-earlier" },
        };

        var started = await RunStartedAsync(client, [new ChatMessage(ChatRole.User, "Try that again")], options);

        Assert.Equal("wo-1001-thread", started.ThreadId);
        Assert.Equal("run-earlier", started.ParentRunId);
    }

    [Fact]
    public async Task A_caller_supplied_ConversationId_becomes_the_thread_id()
    {
        var model = new RecordedChatClient(RecordedChatClient.TextTurn("msg-1", "Hello."));
        await using var agent = new AgentFactory(model);
        using IChatClient client = new AGUIChatClient(new AGUIChatClientOptions(agent.CreateClient(), "/agent"));

        var started = await RunStartedAsync(
            client,
            [new ChatMessage(ChatRole.User, "Hello") { MessageId = "user-1" }],
            new ChatOptions { ConversationId = "wo-1001-thread" });

        Assert.Equal("wo-1001-thread", started.ThreadId);
        Assert.True(model.Calls[0].Options!.TryGetRunAgentInput(out var input));
        Assert.Equal("wo-1001-thread", input.ThreadId);
    }

    // AGUIChatClient reads with ResponseHeadersRead, so HttpClient.Timeout bounds only the wait for
    // headers, and AGUI.Server sends none until the model's first update arrives.
    [Fact]
    public async Task HttpClient_Timeout_bounds_only_the_wait_for_response_headers()
    {
        var model = new PacedChatClient();
        await using var agent = new AgentFactory(model);

        // TestServer answers an aborted request with a 499 instead of letting HttpClient time out, so
        // this test runs the agent on Kestrel, on a random port.
        agent.UseKestrel(0);
        agent.StartServer();
        var baseAddress = agent.CreateClient().BaseAddress;
        using (var warmUp = new HttpClient { BaseAddress = baseAddress })
        {
            await AnswerAsync(warmUp); // Warm the host up with no pauses.
        }

        // A model that takes longer than the timeout to start: no headers, so the request times out.
        model.BeforeFirstUpdate = TimeSpan.FromSeconds(5);
        using var slowToStart = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(1) };
        var timedOut = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AnswerAsync(slowToStart));
        Assert.False(Ct.IsCancellationRequested);
        Assert.IsType<TimeoutException>(timedOut.InnerException);

        // A model that starts at once, then pauses longer than the timeout: the stream completes.
        model.BeforeFirstUpdate = TimeSpan.Zero;
        model.BetweenUpdates = TimeSpan.FromSeconds(3);
        using var pausesMidStream = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(1) };
        Assert.Equal("Checking WO-1001.", await AnswerAsync(pausesMidStream));

        static async Task<string> AnswerAsync(HttpClient http)
        {
            using IChatClient client = new AGUIChatClient(new AGUIChatClientOptions(http, "/agent"));
            var text = new StringBuilder();
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "What is the status of WO-1001?") { MessageId = "user-1" }], cancellationToken: Ct))
            {
                text.Append(update.Text);
            }

            return text.ToString();
        }
    }

    // processing.mdx says a consumer strips an unrecognized union member and carries on; AGUI.Client
    // 1.0.0 throws instead, so a legal addition in a later 1.x would end a 1.0.0 client's stream.
    [Theory]
    [InlineData("""{"type":"RUN_FINISHED","threadId":"t-1","runId":"r-1","outcome":{"type":"deferred"}}""")]
    [InlineData("""{"type":"MESSAGES_SNAPSHOT","messages":[{"id":"m-1","role":"narrator","content":"Once upon a time"}]}""")]
    [InlineData("""{"type":"MESSAGES_SNAPSHOT","messages":[{"id":"m-1","role":"user","content":[{"type":"hologram","text":"EQ-501"}]}]}""")]
    [InlineData("""{"type":"MESSAGES_SNAPSHOT","messages":[{"id":"m-1","role":"user","content":[{"type":"image","source":{"type":"carrier-pigeon","value":"EQ-501.png"}}]}]}""")]
    public async Task An_unknown_union_member_ends_the_client_stream(string frame)
    {
        using var http = new HttpClient(new CannedSseHandler(
            """{"type":"RUN_STARTED","threadId":"t-1","runId":"r-1"}""",
            frame,
            """{"type":"RUN_FINISHED","threadId":"t-1","runId":"r-1"}"""));
        using IChatClient client = new AGUIChatClient(new AGUIChatClientOptions(http, "http://beacon.test/agent"));

        await Assert.ThrowsAnyAsync<JsonException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Hello") { MessageId = "user-1" }], cancellationToken: Ct))
            {
            }
        });
    }

    private static async Task<RunStartedEvent> RunStartedAsync(IChatClient client, List<ChatMessage> history, ChatOptions options)
    {
        RunStartedEvent? started = null;
        await foreach (var update in client.GetStreamingResponseAsync(history, options, Ct))
        {
            started ??= update.RawRepresentation as RunStartedEvent;
        }

        return Assert.IsType<RunStartedEvent>(started);
    }
}
