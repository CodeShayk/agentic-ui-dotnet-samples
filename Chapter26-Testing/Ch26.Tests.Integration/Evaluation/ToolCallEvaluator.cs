using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Ch26.Tests.Integration.Evaluation;

/// <summary>The call a good answer makes: the tool, and the arguments that must have these values.</summary>
public sealed class ExpectedToolCall(string toolName, IReadOnlyDictionary<string, string> arguments)
    : EvaluationContext(ContextName, $"{toolName}({string.Join(", ", arguments.Select(a => $"{a.Key}={a.Value}"))})")
{
    public const string ContextName = "Expected tool call";

    public string ToolName { get; } = toolName;

    public IReadOnlyDictionary<string, string> Arguments { get; } = arguments;
}

/// <summary>
/// A deterministic evaluator: did the agent call the right tool with the right arguments? It reads the
/// <see cref="FunctionCallContent"/> in the response the client received, needs no model and gives the
/// same verdict every time for the same response, so it runs offline against recordings and, unchanged,
/// against live models.
/// </summary>
public sealed class ToolCallEvaluator : IEvaluator
{
    public const string MetricName = "Expected tool call";

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var metric = new BooleanMetric(MetricName);
        if (additionalContext?.OfType<ExpectedToolCall>().FirstOrDefault() is not { } expected)
        {
            metric.AddDiagnostics(EvaluationDiagnostic.Error($"Pass an {nameof(ExpectedToolCall)} in the additional context."));
            return new(new EvaluationResult(metric));
        }

        var calls = modelResponse.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        var match = calls.FirstOrDefault(call => call.Name == expected.ToolName && expected.Arguments.All(a => HasValue(call, a.Key, a.Value)));

        metric.Value = match is not null;
        metric.Reason = match is not null
            ? $"Called {Describe(match)}."
            : calls.Count == 0
                ? $"Called no tool; expected {expected.ToolName}."
                : $"Called {string.Join(", ", calls.Select(Describe))}; expected {expected.ToolName} with {string.Join(", ", expected.Arguments.Select(a => $"{a.Key}={a.Value}"))}.";
        metric.Interpretation = new EvaluationMetricInterpretation(
            match is not null ? EvaluationRating.Exceptional : EvaluationRating.Unacceptable,
            failed: match is null,
            reason: metric.Reason);

        return new(new EvaluationResult(metric));
    }

    // Models send arguments as numbers or strings; "1" and 1 name the same quantity.
    private static bool HasValue(FunctionCallContent call, string name, string expected) =>
        call.Arguments?.TryGetValue(name, out var value) == true
        && string.Equals(Text(value), expected, StringComparison.OrdinalIgnoreCase);

    private static string? Text(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement e => e.GetRawText(),
        _ => value?.ToString(),
    };

    private static string Describe(FunctionCallContent call) =>
        $"{call.Name}({string.Join(", ", (call.Arguments ?? new Dictionary<string, object?>()).Select(a => $"{a.Key}={Text(a.Value)}"))})";
}
