using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;

namespace Ch27.SmokeTest;

/// <summary>One check and what it saw.</summary>
public sealed record SmokeCheck(string Name, bool Passed, string Detail);

/// <summary>Every check of one smoke run.</summary>
public sealed record SmokeReport(IReadOnlyList<SmokeCheck> Checks)
{
    public bool Passed => Checks.Count > 0 && Checks.All(check => check.Passed);
}

/// <summary>
/// Streams one run through whatever sits between this process and the agent (a dev proxy, an
/// ingress, a gateway) and checks what a dispatcher would notice: the agent is ready, it refuses a run
/// without a token, the run completes, and its events arrive as they are produced rather than all at once.
/// </summary>
public static class StreamSmokeTest
{
    public const string Question = "What is the status of WO-1001?";

    /// <summary>Frames that all arrive within this window were held back and released together.</summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromMilliseconds(50);

    public static async Task<SmokeReport> RunAsync(HttpClient agent, string? token, CancellationToken cancellationToken)
    {
        List<SmokeCheck> checks = [];
        var clock = Stopwatch.StartNew();

        // 1. Readiness, as the platform's probe asks for it.
        using (var health = await agent.GetAsync("/health", cancellationToken))
        {
            var status = await health.Content.ReadAsStringAsync(cancellationToken);
            checks.Add(new("GET /health", health.IsSuccessStatusCode,
                $"{(int)health.StatusCode} {status} in {clock.ElapsedMilliseconds} ms"));
        }

        // 2. A run without a token must be refused before anything runs.
        using (var anonymous = await agent.SendAsync(RunRequest(token: null), cancellationToken))
        {
            checks.Add(new("POST /agent, no token", anonymous.StatusCode == HttpStatusCode.Unauthorized,
                $"{(int)anonymous.StatusCode} {anonymous.Headers.WwwAuthenticate}"));
        }

        // 3. One run, read frame by frame, recording when each frame arrived.
        using var request = RunRequest(token);
        clock.Restart();
        using var response = await agent.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var length = response.Content.Headers.ContentLength;
        checks.Add(new("POST /agent", response.IsSuccessStatusCode && mediaType == "text/event-stream",
            $"{(int)response.StatusCode} {mediaType}, headers after {clock.ElapsedMilliseconds} ms, "
            + (length is null ? "no Content-Length" : $"Content-Length {length}")));

        if (!response.IsSuccessStatusCode)
        {
            return new(checks);
        }

        List<(string Type, TimeSpan At)> frames = [];
        await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
            {
                using var json = JsonDocument.Parse(item.Data);
                frames.Add((json.RootElement.GetProperty("type").GetString() ?? "?", clock.Elapsed));
            }
        }

        // 4. The run is complete: it starts, says something and ends with RUN_FINISHED.
        var first = frames.Count > 0 ? frames[0].Type : "(none)";
        var last = frames.Count > 0 ? frames[^1].Type : "(none)";
        var deltas = frames.Count(frame => frame.Type == "TEXT_MESSAGE_CONTENT");
        checks.Add(new("run", first == "RUN_STARTED" && last == "RUN_FINISHED" && deltas > 0,
            $"{frames.Count} events, {first} to {last}, {deltas} text deltas"));

        // 5. The events streamed. A buffering proxy delivers every frame in one burst at the end. A heuristic: a
        //    proxy that flushes in chunks passes it, which the largest gap between two frames helps spot.
        if (frames.Count >= 5)
        {
            var spread = frames[^1].At - frames[0].At;
            var largestGap = frames.Zip(frames.Skip(1), (a, b) => b.At - a.At).Max();
            checks.Add(new("incremental delivery", spread >= BurstWindow,
                $"first event at {frames[0].At.TotalMilliseconds:0} ms, last at {frames[^1].At.TotalMilliseconds:0} ms, "
                + $"largest gap {largestGap.TotalMilliseconds:0} ms"));
        }

        return new(checks);
    }

    private static HttpRequestMessage RunRequest(string? token)
    {
        var runId = $"smoke-{Guid.NewGuid():N}";
        var input = JsonSerializer.Serialize(new
        {
            threadId = $"smoke-thread-{runId[6..14]}",
            runId,
            messages = new[] { new { id = "smoke-m-1", role = "user", content = Question } },
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = new StringContent(input, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }
}
