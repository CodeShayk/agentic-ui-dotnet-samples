using Beacon.Testing;

namespace Ch05.Tests;

/// <summary>
/// The same contract checks against a real local model. Skipped unless RUN_MODEL_TESTS=true;
/// expects Ollama at http://localhost:11434 with llama3.2 pulled (override with AI__OllamaEndpoint
/// and AI__OllamaModel). Real models vary from run to run, so these assert rules, not words.
/// </summary>
public sealed class LiveModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_MODEL_TESTS"), "true", StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData(HostKind.Plain)]
    [InlineData(HostKind.AgentFramework)]
    public async Task A_local_model_keeps_the_wire_contract(HostKind kind)
    {
        Assert.SkipUnless(Enabled, "Set RUN_MODEL_TESTS=true to run tests against a live model.");

        var endpoint = new Uri(Environment.GetEnvironmentVariable("AI__OllamaEndpoint") ?? "http://localhost:11434");
        await using var host = AgentHosts.Start(
            kind,
            AgentHosts.Ollama(endpoint),
            ("AI:OllamaModel", Environment.GetEnvironmentVariable("AI__OllamaModel") ?? "llama3.2"));

        var events = await AGUIWire.RunAsync(
            host.CreateClient(), "/agent", AGUIWire.UserRun("What is the status of WO-1001?"), Ct);

        Wire.AssertWellFormed(events, "thread-1", "run-1");
        Assert.NotEmpty(Wire.Text(events));
    }
}
