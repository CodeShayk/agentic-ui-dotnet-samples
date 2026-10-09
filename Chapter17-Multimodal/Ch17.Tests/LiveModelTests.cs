using Beacon.Testing;

namespace Ch17.Tests;

/// <summary>
/// The nameplate photo read by a real vision-capable model instead of the scripted one. Live only:
/// skipped unless RUN_MODEL_TESTS=true, and the provider comes from the usual configuration, for example
/// AI__Provider=Ollama with AI__OllamaModel set to a vision model, or AI__Provider=AzureOpenAI.
/// </summary>
public sealed class LiveModelTests
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_MODEL_TESTS"), "true", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task A_vision_model_reads_the_serial_number_from_the_photo()
    {
        Assert.SkipUnless(Enabled, "Set RUN_MODEL_TESTS=true (and a vision-capable AI provider) to run tests against a live model.");
        var ct = TestContext.Current.CancellationToken;
        await using var agent = new AgentHost();

        var frames = await AGUIWire.RunRawAsync(agent.CreateClient(), "/agent",
            Runs.User(Runs.Text("What serial number is printed on this nameplate? Answer with the serial number only."), Runs.Image(Samples.Nameplate)), ct);

        Assert.Contains("2207-1184", Sse.Text(frames), StringComparison.Ordinal);
    }
}
