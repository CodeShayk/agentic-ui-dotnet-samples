using System.Diagnostics.Metrics;

namespace Ch25.Beacon.Agent.Telemetry;

/// <summary>What one model costs, per million tokens. Prices are configuration (<c>Beacon:Pricing</c>), not code.</summary>
public sealed class ModelPrice
{
    public string Model { get; set; } = string.Empty;

    public decimal InputPerMillionTokens { get; set; }

    /// <summary>The price of cache reads; <see langword="null"/> charges them at the input price.</summary>
    public decimal? CachedInputPerMillionTokens { get; set; }

    public decimal OutputPerMillionTokens { get; set; }
}

/// <summary>The price list the run-cost estimate uses.</summary>
public sealed class PricingOptions
{
    public const string SectionName = "Beacon:Pricing";

    public List<ModelPrice> Models { get; set; } = [];

    public ModelPrice? Find(string? model) =>
        Models.FirstOrDefault(p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Beacon's own metrics: the estimated cost of each AG-UI run.</summary>
public sealed class RunCostMetrics
{
    public const string MeterName = "Beacon.Agent";

    private readonly Histogram<double> _runCost;

    public RunCostMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        // Runs cost fractions of a cent, so the default buckets (sized for milliseconds) would put
        // every run in the first one. The advice gives the exporter buckets that fit.
        _runCost = meter.CreateHistogram(
            "beacon.run.cost",
            unit: "{USD}",
            description: "Estimated model cost of one AG-UI run, from its token usage and the configured prices.",
            tags: null,
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.0001, 0.0005, 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1],
            });
    }

    /// <summary>Records one run. Tags stay low-cardinality: never a thread, run or user ID.</summary>
    public void RecordRunCost(double cost, string status) =>
        _runCost.Record(cost, new KeyValuePair<string, object?>("beacon.run.status", status));
}
