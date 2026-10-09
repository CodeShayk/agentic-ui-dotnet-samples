using Microsoft.Extensions.AI.Evaluation;

namespace Ch26.Tests.Integration.Evaluation;

/// <summary>
/// What may fail an evaluation run. Beacon's deterministic evaluators gate: they read the response the Desk
/// received, so a failure is a fact about the agent. Judged scores are reported and never gated, because a
/// judge that can't follow its own score format would turn the run red with no change in the agent.
/// </summary>
public static class EvaluationGate
{
    /// <summary>The metrics that can fail a run.</summary>
    public static readonly IReadOnlyList<string> GatedMetrics = [ToolCallEvaluator.MetricName, ActionClaimEvaluator.MetricName];

    /// <summary>Why the result fails the gate, one line per gated metric; empty when it passes.</summary>
    public static IReadOnlyList<string> Failures(EvaluationResult result)
    {
        List<string> failures = [];
        foreach (var name in GatedMetrics)
        {
            if (!result.Metrics.TryGetValue(name, out var metric))
            {
                failures.Add($"{name}: not evaluated.");
            }
            else if (metric.Interpretation is not { Failed: false })
            {
                failures.Add($"{name}: {metric.Reason}");
            }
        }

        return failures;
    }

    /// <summary>Every metric, gated or not, with its score, verdict and diagnostics: what a person reads.</summary>
    public static IEnumerable<string> Report(EvaluationResult result)
    {
        foreach (var metric in result.Metrics.Values)
        {
            var score = metric switch
            {
                NumericMetric n => n.Value?.ToString("0.#") ?? "-",
                BooleanMetric b => b.Value?.ToString() ?? "-",
                _ => "-",
            };
            yield return $"{metric.Name}: {score} {(metric.Interpretation?.Failed == true ? "FAIL" : "pass")} ({metric.Reason})";
            foreach (var diagnostic in metric.Diagnostics ?? [])
            {
                yield return $"  {diagnostic}";
            }
        }
    }
}
