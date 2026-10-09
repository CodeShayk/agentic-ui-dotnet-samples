using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;

namespace Ch26.Tests.Integration.Evaluation;

/// <summary>The deterministic evaluators are code, so they get unit tests like any other code.</summary>
public sealed class EvaluatorTests
{
    private static readonly ChatMessage[] Question = [new(ChatRole.User, "Order a compressor for WO-1001.")];

    [Fact]
    public void Only_the_deterministic_metrics_can_fail_an_evaluation_run()
    {
        // A judge that left no score (llama3.2 did) and one that scored low: reported, not gated.
        var relevance = new NumericMetric(RelevanceEvaluator.RelevanceMetricName);
        relevance.AddDiagnostics(EvaluationDiagnostic.Error("Failed to parse numeric score for 'Relevance'."));
        var groundedness = new NumericMetric(GroundednessEvaluator.GroundednessMetricName, 2)
        {
            Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Poor, failed: true),
        };
        var judgedPoorly = new EvaluationResult(Verdict(ToolCallEvaluator.MetricName, passed: true), Verdict(ActionClaimEvaluator.MetricName, passed: true), relevance, groundedness);
        Assert.Empty(EvaluationGate.Failures(judgedPoorly));

        var wrongTool = new EvaluationResult(Verdict(ToolCallEvaluator.MetricName, passed: false, "Called list_work_orders(status=Open)."), Verdict(ActionClaimEvaluator.MetricName, passed: true));
        Assert.Equal(["Expected tool call: Called list_work_orders(status=Open)."], EvaluationGate.Failures(wrongTool));

        var notEvaluated = new EvaluationResult(Verdict(ToolCallEvaluator.MetricName, passed: true));
        Assert.Equal(["Unbacked action claim: not evaluated."], EvaluationGate.Failures(notEvaluated));
    }

    private static BooleanMetric Verdict(string name, bool passed, string? reason = null) => new(name, passed, reason)
    {
        Interpretation = new EvaluationMetricInterpretation(passed ? EvaluationRating.Exceptional : EvaluationRating.Unacceptable, failed: !passed, reason),
    };

    [Fact]
    public async Task A_matching_call_passes_and_a_string_number_counts_as_the_number()
    {
        var response = Response(new FunctionCallContent("c1", "place_part_order", new Dictionary<string, object?>
        {
            ["workOrderId"] = "WO-1001",
            ["quantity"] = JsonSerializer.SerializeToElement("1"),
        }));

        var result = await new ToolCallEvaluator().EvaluateAsync(Question, response, additionalContext:
            [new ExpectedToolCall("place_part_order", new Dictionary<string, string> { ["workOrderId"] = "WO-1001", ["quantity"] = "1" })],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Get<BooleanMetric>(ToolCallEvaluator.MetricName).Value);
    }

    [Fact]
    public async Task The_wrong_tool_fails_and_says_what_was_called()
    {
        var response = Response(new FunctionCallContent("c1", "list_work_orders", new Dictionary<string, object?> { ["status"] = "Open" }));

        var result = await new ToolCallEvaluator().EvaluateAsync(Question, response, additionalContext:
            [new ExpectedToolCall("get_work_order", new Dictionary<string, string> { ["workOrderId"] = "WO-1001" })],
            cancellationToken: TestContext.Current.CancellationToken);

        var metric = result.Get<BooleanMetric>(ToolCallEvaluator.MetricName);
        Assert.True(metric.Interpretation!.Failed);
        Assert.Equal("Called list_work_orders(status=Open); expected get_work_order with workOrderId=WO-1001.", metric.Reason);
    }

    [Fact]
    public async Task Saying_an_order_was_placed_without_a_successful_tool_result_is_flagged()
    {
        var claimed = await new ActionClaimEvaluator().EvaluateAsync(Question, Response(new TextContent("Done: the compressor is ordered.")),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(claimed.Get<BooleanMetric>(ActionClaimEvaluator.MetricName).Interpretation!.Failed);

        var backed = await new ActionClaimEvaluator().EvaluateAsync(Question, Response(
                new FunctionCallContent("c1", "place_part_order"),
                new FunctionResultContent("c1", JsonSerializer.SerializeToElement(new { id = "PO-9001" })),
                new TextContent("Order PO-9001 is placed.")),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(backed.Get<BooleanMetric>(ActionClaimEvaluator.MetricName).Interpretation!.Failed);

        var refused = await new ActionClaimEvaluator().EvaluateAsync(Question, Response(
                new FunctionCallContent("c1", "place_part_order"),
                new FunctionResultContent("c1", JsonSerializer.SerializeToElement(new { error = "Not approved." })),
                new TextContent("The order was placed.")),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(refused.Get<BooleanMetric>(ActionClaimEvaluator.MetricName).Interpretation!.Failed);
    }

    private static ChatResponse Response(params AIContent[] contents) => new(new ChatMessage(ChatRole.Assistant, contents));
}
