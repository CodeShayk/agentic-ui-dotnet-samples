using Ch16.Beacon.Agent;
using Ch16.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ch16.Tests;

/// <summary>
/// The same reasoning path against a real thinking model through Ollama. Skipped unless RUN_MODEL_TESTS=true;
/// the model is CH16_OLLAMA_MODEL (default qwen3.6), which must be pulled locally. A large model on a laptop
/// can take many minutes to answer.
/// </summary>
public sealed class LiveModelTests
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_MODEL_TESTS"), "true", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task A_thinking_model_through_Ollama_streams_its_thinking_as_reasoning_and_seals_nothing()
    {
        Assert.SkipUnless(Enabled, "Set RUN_MODEL_TESTS=true to run tests against a live model.");

        await using var host = new LiveAgentHost(Environment.GetEnvironmentVariable("CH16_OLLAMA_MODEL") ?? "qwen3.6");
        using var http = host.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(20);

        var frames = await Runs.RawAsync(http, Runs.Turn("t-live", "r-1",
            Runs.User("u-1", "An air conditioner short-cycles and blows warm air. In two sentences, what is the most likely cause?")));

        var types = frames.Select(Runs.Type).ToList();
        Assert.Contains("REASONING_MESSAGE_CONTENT", types);
        Assert.True(types.IndexOf("REASONING_END") < types.IndexOf("TEXT_MESSAGE_START"), "the thinking closes before the answer opens");

        // Ollama returns thinking as text. There is nothing sealed to carry, and no separate reasoning count.
        Assert.DoesNotContain("REASONING_ENCRYPTED_VALUE", types);
        Assert.Equal("RUN_FINISHED", types[^1]);
    }

    private sealed class LiveAgentHost(string model) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI:Provider", "Ollama");
            builder.UseSetting("AI:OllamaModel", model);
            builder.UseSetting("AI:TimeoutSeconds", "1200");
            builder.UseSetting("Beacon:Reasoning:Disclosure", "Full");
        }
    }
}
