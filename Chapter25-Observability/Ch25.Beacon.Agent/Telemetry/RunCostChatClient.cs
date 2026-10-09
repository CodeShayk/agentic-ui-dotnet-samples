using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ch25.Beacon.Agent.Telemetry;

/// <summary>
/// Prices each run from the <see cref="UsageContent"/> its model calls report, the same counts the
/// AG-UI converter adds up into <c>RUN_FINISHED.usage</c>. Place it above function invocation so it
/// sees every model call in the run. It records the <c>beacon.run.cost</c> metric and tags the run's
/// span; it never changes an update.
/// </summary>
public sealed partial class RunCostChatClient(
    IChatClient innerClient,
    PricingOptions pricing,
    RunCostMetrics metrics,
    ILogger<RunCostChatClient> logger) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The AG-UI converter starts the agui.run span and then asks for the first update,
        // so here, at the start of the run, the current span is the run's.
        var runSpan = Activity.Current;
        Dictionary<string, UsageDetails> usageByModel = [];
        var completed = false;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                foreach (var usage in update.Contents.OfType<UsageContent>())
                {
                    Add(usageByModel, update.ModelId, usage.Details);
                }

                yield return update;
            }

            completed = true;
        }
        finally
        {
            // A failed or stopped run still spent what its model calls reported.
            var status = completed ? "completed" : cancellationToken.IsCancellationRequested ? "canceled" : "failed";
            Record(usageByModel, status, runSpan);
        }
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var runSpan = Activity.Current;
        Dictionary<string, UsageDetails> usageByModel = [];
        var completed = false;
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            if (response.Usage is { } usage)
            {
                Add(usageByModel, response.ModelId, usage);
            }

            completed = true;
            return response;
        }
        finally
        {
            var status = completed ? "completed" : cancellationToken.IsCancellationRequested ? "canceled" : "failed";
            Record(usageByModel, status, runSpan);
        }
    }

    private static void Add(Dictionary<string, UsageDetails> usageByModel, string? model, UsageDetails details)
    {
        var key = model ?? string.Empty;
        if (!usageByModel.TryGetValue(key, out var total))
        {
            usageByModel[key] = total = new UsageDetails();
        }

        total.Add(details);
    }

    private void Record(Dictionary<string, UsageDetails> usageByModel, string status, Activity? runSpan)
    {
        // No usage reported means unknown, not free: record nothing rather than a zero.
        if (usageByModel.Count == 0)
        {
            return;
        }

        decimal cost = 0;
        foreach (var (model, usage) in usageByModel)
        {
            if (pricing.Find(model) is not { } price)
            {
                // A partial sum would understate the run, so an unpriced model means no estimate.
                LogUnpriced(logger, model);
                return;
            }

            long input = usage.InputTokenCount ?? 0, cached = usage.CachedInputTokenCount ?? 0, output = usage.OutputTokenCount ?? 0;
            cost += ((input - cached) * price.InputPerMillionTokens
                + cached * (price.CachedInputPerMillionTokens ?? price.InputPerMillionTokens)
                + output * price.OutputPerMillionTokens) / 1_000_000m;
        }

        metrics.RecordRunCost((double)cost, status);

        runSpan?.SetTag("beacon.run.cost", (double)cost);
        runSpan?.SetTag("beacon.run.input_tokens", usageByModel.Values.Sum(u => u.InputTokenCount ?? 0));
        runSpan?.SetTag("beacon.run.output_tokens", usageByModel.Values.Sum(u => u.OutputTokenCount ?? 0));
    }

    [LoggerMessage(EventId = 2501, Level = LogLevel.Warning,
        Message = "No price is configured for model '{Model}' in Beacon:Pricing; the run's cost was not recorded.")]
    private static partial void LogUnpriced(ILogger logger, string model);
}

public static class RunCostChatClientBuilderExtensions
{
    /// <summary>Adds <see cref="RunCostChatClient"/> with the prices from <c>Beacon:Pricing</c>.</summary>
    public static ChatClientBuilder UseRunCost(this ChatClientBuilder builder) =>
        builder.Use((innerClient, services) => new RunCostChatClient(
            innerClient,
            services.GetRequiredService<IOptions<PricingOptions>>().Value,
            services.GetRequiredService<RunCostMetrics>(),
            services.GetRequiredService<ILogger<RunCostChatClient>>()));
}
