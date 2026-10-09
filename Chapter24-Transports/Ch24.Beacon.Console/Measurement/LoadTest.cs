using System.Diagnostics;
using Ch24.Beacon.Console.Transport;

namespace Ch24.Beacon.Console.Measurement;

/// <summary>A load test's shape: who connects, how, and what counts as fast enough.</summary>
public sealed record LoadTestOptions(
    string Endpoint,
    int Users,
    int RunsPerUser,
    WireFormat Wire,
    bool Http2,
    bool MultipleHttp2Connections,
    bool Retry,
    TimeSpan FirstTextBudget,
    string Question);

/// <summary>Percentiles of one milestone across every run.</summary>
public sealed record Distribution(TimeSpan P50, TimeSpan P95, TimeSpan P99, TimeSpan Max)
{
    public static Distribution Of(IEnumerable<TimeSpan> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        }

        // Nearest rank: the smallest value with at least p percent of the runs at or below it.
        TimeSpan At(double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1)];
        return new(At(0.50), At(0.95), At(0.99), sorted[^1]);
    }

    public override string ToString() =>
        $"p50 {P50.TotalMilliseconds,7:0.0} ms   p95 {P95.TotalMilliseconds,7:0.0} ms   p99 {P99.TotalMilliseconds,7:0.0} ms   max {Max.TotalMilliseconds,7:0.0} ms";
}

/// <summary>What a load test found.</summary>
public sealed record LoadTestReport(
    LoadTestOptions Options,
    int Runs,
    int Succeeded,
    IReadOnlyDictionary<string, int> Outcomes,
    int Retried,
    Distribution Headers,
    Distribution RunStarted,
    Distribution FirstText,
    Distribution Finished,
    double BytesPerRun,
    TimeSpan Elapsed,
    string HttpVersions)
{
    /// <summary>Every run succeeded and 95 percent of them showed text within the budget.</summary>
    public bool WithinBudget => Succeeded == Runs && FirstText.P95 <= Options.FirstTextBudget;
}

/// <summary>
/// A small, honest load test: N concurrent users, each asking M questions in a row through
/// <c>AGUIChatClient</c>, every run measured from the moment it was asked.
/// </summary>
public static class LoadTest
{
    public static async Task<LoadTestReport> RunAsync(LoadTestOptions options, CancellationToken cancellationToken = default)
    {
        using var http = BeaconHttp.Create(options.Wire, options.Http2, options.MultipleHttp2Connections);

        // Warm up: JIT, the first connection and the server's first-request costs stay out of the numbers.
        for (var i = 0; i < 3; i++)
        {
            await RunProbe.RunAsync(http, options.Endpoint, options.Question, cancellationToken: cancellationToken);
        }

        var runs = new List<RunMeter>[options.Users];
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();

        var users = Enumerable.Range(0, options.Users).Select(user => Task.Run(async () =>
        {
            runs[user] = [];
            await start.Task;
            for (var i = 0; i < options.RunsPerUser; i++)
            {
                runs[user].Add(await RunProbe.RunAsync(http, options.Endpoint, options.Question, options.Retry, cancellationToken: cancellationToken));
            }
        }, cancellationToken)).ToList();

        clock.Restart();
        start.SetResult();
        await Task.WhenAll(users);
        var elapsed = clock.Elapsed;

        var all = runs.SelectMany(r => r).ToList();
        var good = all.Where(r => r.Outcome == "success").ToList();

        return new LoadTestReport(
            options,
            Runs: all.Count,
            Succeeded: good.Count,
            Outcomes: all.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count()),
            Retried: all.Count(r => r.Attempts > 1),
            Headers: Distribution.Of(good.Select(r => r.Headers!.Value)),
            RunStarted: Distribution.Of(good.Select(r => r.RunStarted!.Value)),
            FirstText: Distribution.Of(good.Select(r => r.FirstText ?? r.Finished!.Value)),
            Finished: Distribution.Of(good.Select(r => r.Finished!.Value)),
            BytesPerRun: good.Count > 0 ? good.Average(r => r.Bytes) : 0,
            Elapsed: elapsed,
            HttpVersions: string.Join(", ", good.Select(r => r.HttpVersion?.ToString()).Distinct()));
    }
}
