using Beacon.AI;
using Ch26.Tests.Integration.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using Microsoft.Extensions.Configuration;

namespace Ch26.Tests.Integration.Evaluation;

/// <summary>
/// Section 26.6: is the agent's behavior right, not only well formed? The deterministic evaluators run on
/// every build against recorded runs. With RUN_MODEL_TESTS=true the same cases run against a live model,
/// scored by the deterministic evaluators and by Microsoft.Extensions.AI's quality evaluators, which use a
/// model as the judge.
/// </summary>
public sealed class AgentEvaluationTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool LiveEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_MODEL_TESTS"), "true", StringComparison.OrdinalIgnoreCase);

    private static readonly IEvaluator Deterministic = new CompositeEvaluator(new ToolCallEvaluator(), new ActionClaimEvaluator());

    // One execution per test run, so the report groups every scenario this run evaluated.
    private static readonly string ExecutionName = $"beacon-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

    [Theory]
    [MemberData(nameof(EvaluationCases.Names), MemberType = typeof(EvaluationCases))]
    public async Task Recorded_runs_meet_the_deterministic_bar(string name)
    {
        var evaluationCase = EvaluationCases.Named(name);
        await using var agent = await AgentHost.ReplayingAsync(evaluationCase.Recording);

        var response = await EvaluationCases.AskAsync(agent, evaluationCase.Question, Ct);
        var result = await Deterministic.EvaluateAsync(
            [new ChatMessage(ChatRole.User, evaluationCase.Question)], response, additionalContext: [evaluationCase.Expected], cancellationToken: Ct);

        foreach (var metric in result.Metrics.Values)
        {
            output.WriteLine($"{name} | {metric.Name}: {(metric.Interpretation!.Failed ? "FAIL" : "pass")} ({metric.Reason})");
        }

        Assert.Equal(evaluationCase.ExpectedToPass, !result.Metrics.Values.Any(m => m.Interpretation!.Failed));
    }

    [Theory]
    [InlineData("What is the status of WO-1001?", "get_work_order", "workOrderId", "WO-1001")]
    [InlineData("Is EQ-501 still under warranty?", "check_warranty", "equipmentId", "EQ-501")]
    public async Task A_live_model_meets_the_quality_bar(string question, string tool, string argument, string value)
    {
        Assert.SkipUnless(LiveEnabled, "Set RUN_MODEL_TESTS=true (and an AI provider, Ollama by default) to evaluate a live model.");

        var configuration = LiveConfiguration();
        await using var agent = new AgentHost(AIClientFactory.CreateChatClient(configuration));
        var response = await EvaluationCases.AskAsync(agent, question, Ct);

        // The judge: a model scoring the answer with Microsoft.Extensions.AI.Evaluation's quality prompts.
        // Its responses are cached on disk, so re-evaluating the same answer costs nothing and scores the same.
        using var judge = AIClientFactory.CreateChatClient(JudgeConfiguration(configuration));
        var reporting = DiskBasedReportingConfiguration.Create(
            storageRootPath: ResultsDirectory,
            evaluators: [new ToolCallEvaluator(), new ActionClaimEvaluator(), new RelevanceEvaluator(), new GroundednessEvaluator()],
            chatConfiguration: new ChatConfiguration(judge),
            enableResponseCaching: true,
            executionName: ExecutionName);

        await using var scenario = await reporting.CreateScenarioRunAsync($"{tool}-{value}", cancellationToken: Ct);
        var result = await scenario.EvaluateAsync(
            [new ChatMessage(ChatRole.User, question)],
            response,
            additionalContext:
            [
                new ExpectedToolCall(tool, new Dictionary<string, string> { [argument] = value }),
                new GroundednessEvaluatorContext(EvaluationCases.GroundingOf(response)),
            ],
            cancellationToken: Ct);

        output.WriteLine($"Answer: {response.Text}");
        foreach (var line in EvaluationGate.Report(result))
        {
            output.WriteLine(line);
        }

        // Only the deterministic metrics gate. The judged scores go to this output and to the stored results
        // that dotnet aieval report renders, where a person reads them next to the answer.
        Assert.Empty(EvaluationGate.Failures(result));
    }

    /// <summary>Where evaluation results and cached judge responses go; <c>dotnet aieval report --path</c> reads them.</summary>
    private static string ResultsDirectory =>
        Environment.GetEnvironmentVariable("BEACON_EVALUATION_RESULTS") is { Length: > 0 } path
            ? path
            : Path.Combine(AppContext.BaseDirectory, "evaluation-results");

    // Ollama with llama3.2 unless the environment says otherwise (AI__Provider, AI__OllamaModel, ...).
    private static IConfiguration LiveConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:Provider"] = "Ollama",
            ["AI:OllamaEndpoint"] = "http://localhost:11434",
            ["AI:OllamaModel"] = "llama3.2",
        })
        .AddEnvironmentVariables()
        .Build();

    // The judge defaults to the agent's model; Judge__Provider, Judge__Model and friends choose another.
    private static IConfiguration JudgeConfiguration(IConfiguration live)
    {
        var judge = live.GetSection("Judge");
        if (!judge.Exists())
        {
            return live;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(judge.AsEnumerable(makePathsRelative: true)
                .Where(p => p.Value is not null)
                .Select(p => KeyValuePair.Create($"AI:{p.Key}", p.Value)))
            .Build();
    }
}
