using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Formatting;
using Ch23.Beacon.Agent;
using Ch23.Beacon.Security.Approvals;
using Ch23.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch23.Tests;

/// <summary>Chapter 22's thread API, behind the policy and scoped to the caller: another user's thread doesn't exist.</summary>
public sealed class ThreadApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_thread_API_without_a_token_is_401()
    {
        await using var host = Hosts.Agent();

        using var response = await host.CreateClient(token: null).GetAsync("/agent/threads", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Another_users_thread_is_not_listed_not_readable_and_not_deletable()
    {
        await using var host = Hosts.Agent();
        var dana = host.CreateClient(TestTokens.Dana);
        var priya = host.CreateClient(TestTokens.Priya);
        await Runs.RunAsync(dana, Runs.Turn("t-dana", "run-1", Runs.User("user-1", "What is the status of WO-1001?")), Ct);

        // Priya's list is hers alone, and Dana's thread is a 404 to her whether she reads it or deletes it.
        using (var list = JsonDocument.Parse(await priya.GetStringAsync("/agent/threads", Ct)))
        {
            Assert.Equal(0, list.RootElement.GetArrayLength());
        }

        using (var read = await priya.GetAsync("/agent/threads/t-dana/events", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        }

        using (var delete = await priya.DeleteAsync("/agent/threads/t-dana", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }

        // Dana still has it.
        using var mine = JsonDocument.Parse(await dana.GetStringAsync("/agent/threads", Ct));
        Assert.Equal("t-dana", Assert.Single(mine.RootElement.EnumerateArray()).GetProperty("threadId").GetString());
    }

    [Fact]
    public async Task A_restored_thread_brings_back_the_transcript_and_the_open_approval()
    {
        await using var host = Hosts.Agent();
        var dana = host.CreateClient(TestTokens.Dana);
        var (interrupt, _) = await ApprovalEnforcementTests.AskForCompressorAsync(dana);

        var events = await ReadEventsAsync(dana, "/agent/threads/thread-1/events");

        Assert.Equal(["RUN_STARTED", "MESSAGES_SNAPSHOT", "RUN_FINISHED"], events.Select(e => e.Type));
        Assert.Contains(Assert.IsType<MessagesSnapshotEvent>(events[1]).Messages, m => m is AGUIUserMessage u && u.Content.ToString() == "Order CMP-CA36 for WO-1001");
        Assert.Equal(interrupt.Id, Runs.SingleInterrupt(events).Id);

        // And the restored question can be answered as if it had arrived live.
        await Runs.RunAsync(dana, ApprovalEnforcementTests.Resume("run-2", Runs.Answer(interrupt.Id, true)), Ct);
        Assert.Single(ApprovalEnforcementTests.Store(host).ListPartOrders());
    }

    [Fact]
    public async Task A_parent_run_other_than_the_latest_is_409_because_the_transcript_is_linear()
    {
        await using var host = Hosts.Agent();
        var dana = host.CreateClient(TestTokens.Dana);
        await Runs.RunAsync(dana, Runs.Turn("thread-1", "run-1", Runs.User("user-1", "What is the status of WO-1001?")), Ct);
        await Runs.RunAsync(dana, Runs.Turn("thread-1", "run-2", Runs.User("user-2", "Is EQ-501 under warranty?")), Ct);

        var branch = Runs.Turn("thread-1", "run-3", Runs.User("user-3", "Is EQ-501 under warranty?"));
        branch.ParentRunId = "run-1";
        using var response = await Runs.PostAsync(dana, branch, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Not the latest run", (await Runs.ProblemAsync(response, Ct)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_run_the_client_stopped_is_restated_as_cancelled_not_success()
    {
        await using var host = Hosts.Agent(new Dictionary<string, string?> { ["Beacon:Security:RunTimeoutSeconds"] = "60" }, new StallingChatClient());
        var dana = host.CreateClient(TestTokens.Dana);

        // The run starts streaming, then Dana presses Stop: the request is abandoned mid-stream.
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            using var response = await Runs.PostAsync(dana, Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Hello")), stop.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            stop.Cancel();
        }

        var ending = (await RestoredAsync(dana, "/agent/threads/thread-1/events"))[^1];
        Assert.IsType<RunFinishedCancelledOutcome>(Assert.IsType<RunFinishedEvent>(ending).Outcome);
    }

    [Fact]
    public async Task A_run_that_broke_under_the_server_is_restated_as_RUN_ERROR()
    {
        // The approval store fails while the run is ending: the stream breaks before RUN_FINISHED goes out.
        await using var host = Hosts.Agent(services: services => services.AddSingleton<IApprovalStore>(
            sp => new FailingToOpenApprovalStore(new InMemoryApprovalStore(sp.GetRequiredService<TimeProvider>()))));
        var dana = host.CreateClient(TestTokens.Dana);

        try
        {
            using var response = await Runs.PostAsync(dana, Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Order CMP-CA36 for WO-1001")), Ct);
            await response.Content.ReadAsStringAsync(Ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            // The client sees a broken stream; what matters is how the server remembers the run.
        }

        var ending = Assert.IsType<RunErrorEvent>((await RestoredAsync(dana, "/agent/threads/thread-1/events"))[^1]);
        Assert.Equal(ThreadEndpoints.BrokenRunCode, ending.Code);
    }

    // The run is recorded when its stream ends, which can be just after the client stopped reading.
    private static async Task<IReadOnlyList<BaseEvent>> RestoredAsync(HttpClient http, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var probe = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, Ct);
            if (probe.StatusCode == HttpStatusCode.OK || attempt == 50)
            {
                return await ReadEventsAsync(http, path);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }
    }

    private static async Task<IReadOnlyList<BaseEvent>> ReadEventsAsync(HttpClient http, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SseEventStreamFormatter.ServerSentEventsMediaType));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        var events = new List<BaseEvent>();
        await foreach (var evt in new SseEventStreamFormatter().ReadAsync(body, Ct))
        {
            events.Add(evt);
        }

        return events;
    }
}
