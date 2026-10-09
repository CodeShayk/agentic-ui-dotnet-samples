using System.Net;
using System.Text;
using Ch02.HandBuilt.Console;

namespace Ch02.Tests;

/// <summary>
/// One response may carry several runs: a replayed run restated as a snapshot, then the requested
/// run (Chapter 3 shows one on the wire). Each run has its own verdict, so the console must report
/// the last run's ending, never one left over from an earlier run.
/// </summary>
public sealed class MultiRunStreamTests
{
    private static readonly string[] ReplayedRun =
    [
        """{"type":"RUN_STARTED","threadId":"thread-1","runId":"run-1"}""",
        """{"type":"MESSAGES_SNAPSHOT","messages":[{"id":"user-1","role":"user","content":"Hi"},{"id":"msg-1","role":"assistant","content":"Hello."}]}""",
        """{"type":"RUN_FINISHED","threadId":"thread-1","runId":"run-1"}""",
    ];

    private static readonly string[] RequestedRunSoFar =
    [
        """{"type":"RUN_STARTED","threadId":"thread-1","runId":"run-2"}""",
        """{"type":"TEXT_MESSAGE_START","messageId":"msg-2","role":"assistant"}""",
        """{"type":"TEXT_MESSAGE_CONTENT","messageId":"msg-2","delta":"The most likely cause is a failing "}""",
    ];

    private const string RequestedRunEnd = """{"type":"TEXT_MESSAGE_END","messageId":"msg-2"}""";
    private const string RequestedRunFinished = """{"type":"RUN_FINISHED","threadId":"thread-1","runId":"run-2"}""";

    [Fact]
    public async Task A_truncated_run_after_a_finished_run_is_reported_as_truncated()
    {
        // The connection drops before the requested run's terminal event.
        using var http = Canned([.. ReplayedRun, .. RequestedRunSoFar]);
        var conversation = new AgentConversation(http, Endpoint);

        var result = await conversation.SendAsync("Why would EQ-501 short-cycle?", _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Truncated, result.Status);

        // The partial answer stays, because the user saw it; the replayed snapshot is ignored.
        Assert.Equal(["user", "assistant"], conversation.History.Select(m => m.Role));
        Assert.Equal("The most likely cause is a failing ", conversation.History[^1].Content);
    }

    [Fact]
    public async Task A_requested_run_that_finishes_after_a_replayed_run_is_finished()
    {
        using var http = Canned([.. ReplayedRun, .. RequestedRunSoFar, RequestedRunEnd, RequestedRunFinished]);
        var conversation = new AgentConversation(http, Endpoint);

        var result = await conversation.SendAsync("Why would EQ-501 short-cycle?", _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Finished, result.Status);
    }

    [Fact]
    public async Task A_late_RUN_ERROR_after_RUN_FINISHED_fails_the_run()
    {
        using var http = Canned(
            [.. RequestedRunSoFar, RequestedRunEnd, RequestedRunFinished, """{"type":"RUN_ERROR","message":"The answer couldn't be saved."}"""]);
        var conversation = new AgentConversation(http, Endpoint);

        var result = await conversation.SendAsync("Why would EQ-501 short-cycle?", _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal("The answer couldn't be saved.", result.Detail);
    }

    private static readonly Uri Endpoint = new("http://localhost/agent");

    /// <summary>An HttpClient whose every response is these events, framed as SSE.</summary>
    private static HttpClient Canned(string[] events) =>
        new(new CannedStream(string.Concat(events.Select(e => $"data: {e}\n\n"))));

    /// <summary>Answers every request with the same SSE body, then closes it.</summary>
    private sealed class CannedStream(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
    }
}
