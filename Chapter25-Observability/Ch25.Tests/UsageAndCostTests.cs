using Beacon.Testing;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;

namespace Ch25.Tests;

/// <summary>Token usage on RUN_FINISHED and the cost of each run.</summary>
public sealed class UsageAndCostTests
{
    // The scripted model's prices in Ch25.Beacon.Agent's appsettings.json (USD per million tokens).
    private const double InputPrice = 0.40;
    private const double OutputPrice = 1.60;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RUN_FINISHED_reports_the_runs_usage_per_provider_and_model()
    {
        await using var host = TracedAgentHost.Start();

        var frames = await AGUIWire.RunRawAsync(
            host.CreateClient(), "/agent", AGUIWire.UserRun("What is the status of WO-1001?", "thread-usage", "run-usage"), Ct);

        var finished = frames[^1];
        Assert.Equal("RUN_FINISHED", finished.GetProperty("type").GetString());
        var usage = Assert.Single(finished.GetProperty("usage").EnumerateArray());
        Assert.Equal("beacon", usage.GetProperty("provider").GetString());
        Assert.Equal("beacon-scripted", usage.GetProperty("model").GetString());

        var input = usage.GetProperty("inputTokens").GetInt64();
        var output = usage.GetProperty("outputTokens").GetInt64();
        Assert.True(input > 0 && output > 0);
        Assert.Equal(input + output, usage.GetProperty("totalTokens").GetInt64());

        // The run's usage is the sum of its model calls, as the chat spans recorded them.
        var trace = await host.WaitForTraceAsync("run-usage", Ct);
        var chats = trace.Where(s => s.DisplayName == "chat").ToList();
        Assert.Equal(2, chats.Count);
        Assert.Equal(input, chats.Sum(c => Convert.ToInt64(c.GetTagItem("gen_ai.usage.input_tokens"))));
        Assert.Equal(output, chats.Sum(c => Convert.ToInt64(c.GetTagItem("gen_ai.usage.output_tokens"))));
    }

    [Fact]
    public async Task Each_run_records_its_cost_as_a_metric_and_on_the_run_span()
    {
        await using var host = TracedAgentHost.Start();

        var frames = await AGUIWire.RunRawAsync(
            host.CreateClient(), "/agent", AGUIWire.UserRun("What is the status of WO-1001?", "thread-cost", "run-cost"), Ct);
        var usage = frames[^1].GetProperty("usage")[0];
        var expected = (usage.GetProperty("inputTokens").GetInt64() * InputPrice
            + usage.GetProperty("outputTokens").GetInt64() * OutputPrice) / 1_000_000;

        var trace = await host.WaitForTraceAsync("run-cost", Ct);
        var run = Assert.Single(trace, s => s.OperationName == "agui.run");
        Assert.Equal(expected, (double)run.GetTagItem("beacon.run.cost")!, precision: 12);

        var cost = Assert.Single(host.CollectMetrics(), m => m.Name == "beacon.run.cost");
        Assert.Equal("{USD}", cost.Unit);
        var point = Assert.Single(cost.MetricPoints);
        Assert.Equal(1, point.GetHistogramCount());
        Assert.Equal(expected, point.GetHistogramSum(), precision: 12);
        Assert.Equal("completed", Tag(point, "beacon.run.status"));

        // Metric tags stay low-cardinality: no thread or run IDs.
        Assert.DoesNotContain("run-cost", Tags(point).Values.Select(v => v?.ToString()));
    }

    [Fact]
    public async Task A_model_without_a_price_records_no_cost_and_says_so()
    {
        // Replace the scripted model's price entry with another model's.
        await using var host = TracedAgentHost.Start(settings: ("Beacon:Pricing:Models:0:Model", "some-other-model"));

        await AGUIWire.RunAsync(
            host.CreateClient(), "/agent", AGUIWire.UserRun("What is the status of WO-1001?", "thread-unpriced", "run-unpriced"), Ct);
        var trace = await host.WaitForTraceAsync("run-unpriced", Ct);

        var run = Assert.Single(trace, s => s.OperationName == "agui.run");
        Assert.Null(run.GetTagItem("beacon.run.cost"));
        Assert.DoesNotContain(host.CollectMetrics(), m => m.Name == "beacon.run.cost");
        Assert.Contains(host.Logs, e => e.Level == LogLevel.Warning && e.Message.Contains("'beacon-scripted'", StringComparison.Ordinal));
    }

    private static object? Tag(MetricPoint point, string key) => Tags(point).GetValueOrDefault(key);

    private static Dictionary<string, object?> Tags(MetricPoint point)
    {
        Dictionary<string, object?> tags = [];
        foreach (var tag in point.Tags)
        {
            tags[tag.Key] = tag.Value;
        }

        return tags;
    }
}
