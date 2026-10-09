using System.Net;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Formatting;
using Beacon.Testing;
using Ch27.Beacon.Agent.Conversations;
using Ch27.Beacon.Agent.Security.History;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ch27.Tests;

/// <summary>Part V's layers, together in one agent: identity, hardening, the durable store, transports and limits.</summary>
public sealed class CombinedAgentTests
{
    private const string Question = "What is the status of WO-1001?";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_without_a_token_is_401_and_nothing_is_stored()
    {
        await using var agent = new AgentHost();

        using var response = await agent.CreateClient(token: null).PostAsJsonRunAsync(AGUIWire.UserRun(Question, "t-anon", "r-anon"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Empty(Directory.EnumerateDirectories(agent.Conversations));
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_401()
    {
        await using var agent = new AgentHost();
        var forged = DevTokens.Create("dana", "Dana Kim", "dispatcher", signingKey: System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        using var response = await agent.CreateClient(forged).PostAsJsonRunAsync(AGUIWire.UserRun(Question), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_development_token_opens_a_stream_that_declares_the_protocol_version_and_carries_no_rawEvent()
    {
        await using var agent = new AgentHost();

        var frames = await AGUIWire.RunRawAsync(agent.CreateClient(DevTokens.Dana), "/agent", AGUIWire.UserRun(Question, "t-wire", "r-wire"), Ct);

        Assert.Equal("RUN_STARTED", frames[0].GetProperty("type").GetString());
        Assert.Equal(AGUIProtocol.Version, frames[0].GetProperty("protocolVersion").GetString());
        Assert.Single(frames, f => f.GetProperty("type").GetString() == "RUN_STARTED"); // the early start, once
        Assert.Equal("RUN_FINISHED", frames[^1].GetProperty("type").GetString());
        Assert.All(frames, f => Assert.False(f.TryGetProperty("rawEvent", out _)));
        Assert.All(frames, f => Assert.Empty(AGUIWire.NullProperties(f)));
    }

    [Fact]
    public async Task Protobuf_is_still_negotiated_and_an_unreadable_Accept_gets_406()
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient(DevTokens.Dana);

        using var protobuf = await PostAsync(http, "application/vnd.ag-ui.event+proto", "r-proto");
        using var json = await PostAsync(http, "application/json", "r-json");

        Assert.Equal(HttpStatusCode.OK, protobuf.StatusCode);
        Assert.Equal("application/vnd.ag-ui.event+proto", protobuf.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotAcceptable, json.StatusCode);

        static Task<HttpResponseMessage> PostAsync(HttpClient http, string accept, string runId)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
            {
                Content = System.Net.Http.Json.JsonContent.Create(AGUIWire.UserRun(Question, "t-" + runId, runId),
                    new JsonSerializerOptions { TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver }.GetTypeInfo(typeof(RunAgentInput))),
            };
            request.Headers.Accept.ParseAdd(accept);
            return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        }
    }

    [Fact]
    public async Task Chapter_23s_approvals_survive_the_combination_a_large_order_waits_for_a_dispatcher()
    {
        await using var agent = new AgentHost();

        var events = await AGUIWire.RunAsync(agent.CreateClient(DevTokens.Dana), "/agent",
            AGUIWire.UserRun("Order CMP-CA36 for WO-1001", "t-order", "r-order"), Ct);

        var finished = Assert.IsType<RunFinishedEvent>(events[^1]);
        var interrupt = Assert.Single(Assert.IsType<RunFinishedInterruptOutcome>(finished.Outcome).Interrupts);
        Assert.StartsWith("apr_", interrupt.Id, StringComparison.Ordinal);
        Assert.Empty(agent.Services.GetRequiredService<global::Beacon.Domain.IFieldServiceStore>().ListPartOrders());
    }

    [Fact]
    public async Task A_conversation_survives_a_restart_and_only_its_owner_can_reopen_it()
    {
        string folder;
        await using (var first = new AgentHost())
        {
            folder = first.Conversations;
            await AGUIWire.RunAsync(first.CreateClient(DevTokens.Dana), "/agent", AGUIWire.UserRun(Question, "t-1001", "r-1"), Ct);
        }

        // A new process over the same folder: what a new revision sees on the mounted volume.
        await using var second = new AgentHost(folder);
        using var dana = await second.CreateClient(DevTokens.Dana).GetAsync("/agent/threads/t-1001/events", Ct);
        using var priya = await second.CreateClient(DevTokens.Priya).GetAsync("/agent/threads/t-1001/events", Ct);

        Assert.Equal(HttpStatusCode.OK, dana.StatusCode);
        var restated = await dana.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"type\":\"MESSAGES_SNAPSHOT\"", restated, StringComparison.Ordinal);
        Assert.Contains(Question, restated, StringComparison.Ordinal);
        Assert.Contains("\"protocolVersion\":\"1.0\"", restated, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, priya.StatusCode); // another user's thread looks like none
    }

    [Fact]
    public async Task The_same_thread_ID_from_two_users_is_two_conversations_on_disk()
    {
        await using var agent = new AgentHost();

        await AGUIWire.RunAsync(agent.CreateClient(DevTokens.Dana), "/agent", AGUIWire.UserRun(Question, "shared", "r-dana"), Ct);
        await AGUIWire.RunAsync(agent.CreateClient(DevTokens.Priya), "/agent", AGUIWire.UserRun("What's on my schedule today?", "shared", "r-priya"), Ct);

        Assert.Equal(2, Directory.EnumerateDirectories(agent.Conversations).Count());
        var priyas = await agent.Store.FindAsync("priya", "shared", Ct);
        Assert.DoesNotContain(priyas!.Messages.OfType<AGUIUserMessage>(), m => m.Content.ToString() == Question);
    }

    [Fact]
    public async Task A_run_ID_runs_once_per_user_even_after_a_restart()
    {
        string folder;
        await using (var first = new AgentHost())
        {
            folder = first.Conversations;
            var dana = first.CreateClient(DevTokens.Dana);
            await AGUIWire.RunAsync(dana, "/agent", AGUIWire.UserRun(Question, "t-once", "r-once"), Ct);

            using var repeat = await dana.PostAsJsonRunAsync(AGUIWire.UserRun(Question, "t-once", "r-once"), Ct);
            Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode); // Chapter 24's ledger

            // The ledger's key includes the user: Priya may pick the same IDs for her own thread.
            using var priya = await first.CreateClient(DevTokens.Priya).PostAsJsonRunAsync(AGUIWire.UserRun("What's on my schedule today?", "t-once", "r-once"), Ct);
            Assert.Equal(HttpStatusCode.OK, priya.StatusCode);
        }

        // A new process has an empty ledger; the store still remembers the run.
        await using var second = new AgentHost(folder);
        using var afterRestart = await second.CreateClient(DevTokens.Dana).PostAsJsonRunAsync(AGUIWire.UserRun(Question, "t-once", "r-once"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, afterRestart.StatusCode);
    }

    [Fact]
    public async Task A_run_found_open_after_a_crash_is_restated_as_RUN_ERROR()
    {
        string folder;
        await using (var first = new AgentHost())
        {
            folder = first.Conversations;
            var store = first.Services.GetRequiredService<FileConversationStore>();

            // A run that started and streamed, then the process died: no commit, the header still names it open.
            var conversation = await store.GetOrCreateAsync("dana", "t-crash", Ct);
            AGUIMessage question = new AGUIUserMessage { Id = "m-1", Content = Question };
            var journal = await store.BeginRunAsync(conversation, "r-crash", [question], Ct);
            await journal.AppendAsync(new RunStartedEvent { ThreadId = "t-crash", RunId = "r-crash" }, Ct);
            await journal.AppendAsync(new TextMessageStartEvent { MessageId = "a-1", Role = "assistant" }, Ct);
            await journal.AppendAsync(new TextMessageContentEvent { MessageId = "a-1", Delta = "WO-1001 is Sched" }, Ct);
            await journal.DisposeAsync();
        }

        await using var second = new AgentHost(folder);
        using var response = await second.CreateClient(DevTokens.Dana).GetAsync("/agent/threads/t-crash/events", Ct);
        var restated = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("WO-1001 is Sched", restated, StringComparison.Ordinal); // what the client was shown
        Assert.Contains("\"type\":\"RUN_ERROR\"", restated, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"RUN_INCOMPLETE\"", restated, StringComparison.Ordinal);
        Assert.Equal(RunEnding.Broken, (await second.Store.FindAsync("dana", "t-crash", Ct))!.LastRunEnding);
    }

    [Fact]
    public async Task Over_the_per_user_limit_a_run_gets_429_with_the_windows_Retry_After()
    {
        await using var agent = AgentHost.With(("Beacon:Security:RunsPerMinute", "1"));
        var dana = agent.CreateClient(DevTokens.Dana);

        await AGUIWire.RunAsync(dana, "/agent", AGUIWire.UserRun(Question, "t-limit", "r-1"), Ct);
        using var second = await dana.PostAsJsonRunAsync(AGUIWire.UserRun(Question, "t-limit", "r-2"), Ct);
        using var priya = await agent.CreateClient(DevTokens.Priya).PostAsJsonRunAsync(AGUIWire.UserRun("What's on my schedule today?", "t-p", "r-p"), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.InRange(second.Headers.RetryAfter!.Delta!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.Equal(HttpStatusCode.OK, priya.StatusCode); // her own budget
    }

    [Fact]
    public async Task Over_the_replica_limit_a_run_gets_503_with_Retry_After_1()
    {
        await using var agent = AgentHost.With(("Beacon:Streaming:MaxConcurrentRuns", "1"), ("AI:ScriptedChunkDelayMilliseconds", "200"));

        // Dana's run holds the only permit while it streams.
        using var first = await agent.CreateClient(DevTokens.Dana).PostAsJsonRunAsync(AGUIWire.UserRun(Question, "t-a", "r-a"), Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await agent.CreateClient(DevTokens.Priya).PostAsJsonRunAsync(AGUIWire.UserRun("What's on my schedule today?", "t-b", "r-b"), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), second.Headers.RetryAfter?.Delta);
    }
}
