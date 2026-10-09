using System.Diagnostics;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Formatting;
using Microsoft.Extensions.AI;

namespace Ch24.Tests;

public sealed class EarlyStartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A model that takes two seconds before each reply's first chunk. The margins are wide on purpose: the
    // assertions compare the early start with the model's silence, not with a fast machine.
    private static readonly TimeSpan ModelSilence = TimeSpan.FromSeconds(2);
    // Task.Delay can end about one timer tick early on Windows (Bible §8), so lower bounds allow 50 ms.
    private static readonly TimeSpan TimerSlack = TimeSpan.FromMilliseconds(50);

    private static AgentHost SlowModel() => new(("Beacon:Simulation:FirstChunkDelay", "00:00:02"));

    [Fact]
    public async Task The_run_opens_before_the_model_answers()
    {
        await using var host = SlowModel();
        using var http = host.CreateClient();

        var clock = Stopwatch.StartNew();
        using var response = await AgentHost.PostAsync(http, AgentHost.Run(), AgentHost.SseType, Ct);
        var headers = clock.Elapsed;

        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        var events = new List<(BaseEvent Event, TimeSpan At)>();
        await foreach (var evt in new SseEventStreamFormatter().ReadAsync(body, Ct))
        {
            events.Add((evt, clock.Elapsed));
        }

        var started = Assert.Single(events, e => e.Event is RunStartedEvent);
        var firstText = events.First(e => e.Event is TextMessageContentEvent);

        Assert.True(headers < ModelSilence / 2, $"Headers took {headers.TotalMilliseconds:0} ms.");
        Assert.True(started.At < ModelSilence / 2, $"RUN_STARTED took {started.At.TotalMilliseconds:0} ms.");
        Assert.True(firstText.At >= 2 * ModelSilence - TimerSlack, "Two silent model calls come before the first text.");
        Assert.Equal("1.0", ((RunStartedEvent)started.Event).ProtocolVersion);
    }

    [Fact]
    public async Task Without_it_the_headers_wait_for_the_model()
    {
        await using var host = SlowModel();
        using var http = host.CreateClient();

        var clock = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent/baseline")
        {
            Content = new StringContent(
                """{"threadId":"thread-24","runId":"run-baseline","messages":[{"id":"user-1","role":"user","content":"What is the status of WO-1001?"}]}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.True(clock.Elapsed >= ModelSilence - TimerSlack, $"Headers arrived after {clock.Elapsed.TotalMilliseconds:0} ms.");
    }

    [Fact]
    public async Task A_header_timeout_shorter_than_the_model_cuts_the_baseline_but_not_this_endpoint()
    {
        await using var host = SlowModel();

        using var http = host.CreateClient();
        http.Timeout = ModelSilence / 2; // shorter than the model's silence

        using var baseline = new AGUIChatClient(new AGUIChatClientOptions(http, "/agent/baseline"));
        var timedOut = await Assert.ThrowsAsync<TaskCanceledException>(() => baseline.GetResponseAsync(Question(), cancellationToken: Ct));
        Assert.IsType<TimeoutException>(timedOut.InnerException);

        using var tuned = new AGUIChatClient(new AGUIChatClientOptions(http, "/agent"));
        var answer = await tuned.GetResponseAsync(Question(), cancellationToken: Ct);
        Assert.Contains("WO-1001", answer.Text, StringComparison.Ordinal);
    }

    private static List<ChatMessage> Question() =>
        [new(ChatRole.User, "What is the status of WO-1001?") { MessageId = $"user_{Guid.NewGuid():N}" }];
}
