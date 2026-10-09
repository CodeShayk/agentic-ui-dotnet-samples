using System.Net.ServerSentEvents;
using System.Text.Json;
using Beacon.Testing;
using Ch27.Beacon.Agent.Hosting;
using Ch27.Beacon.Agent.Security.History;
using Microsoft.Extensions.DependencyInjection;

namespace Ch27.Tests;

public sealed class RunLimitTests
{
    [Fact]
    public async Task A_run_over_its_budget_ends_with_one_RUN_ERROR_that_says_why_and_is_recorded_as_failed()
    {
        // The scripted run takes over a second; the budget is half that.
        await using var agent = AgentHost.With(("Beacon:Hosting:RunBudgetSeconds", "0.5"), ("AI:ScriptedChunkDelayMilliseconds", "100"));
        var ct = TestContext.Current.CancellationToken;

        var frames = await AGUIWire.RunRawAsync(agent.CreateClient(DevTokens.Dana), "/agent",
            AGUIWire.UserRun("What is the status of WO-1001?", "thread-budget", "run-budget"), ct);
        var types = frames.Select(f => f.GetProperty("type").GetString()).ToList();

        Assert.Equal("RUN_STARTED", types[0]);
        Assert.Equal("RUN_ERROR", types[^1]);
        Assert.Single(types, t => t is "RUN_ERROR" or "RUN_FINISHED");
        Assert.Equal(RunGovernor.RunTimeoutCode, frames[^1].GetProperty("code").GetString());
        Assert.Empty(AGUIWire.NullProperties(frames[^1]));

        // One stream-shaping path: the store records the governor's RUN_ERROR as the run's ending.
        var conversation = await agent.Store.FindAsync("dana", "thread-budget", ct);
        Assert.Equal(RunEnding.Failed, conversation!.LastRunEnding);
        Assert.Equal(RunGovernor.RunTimeoutCode, conversation.LastRunError?.Code);
    }

    [Fact]
    public async Task A_run_still_streaming_when_the_drain_ends_gets_RUN_ERROR_SERVER_SHUTDOWN()
    {
        await using var agent = AgentHost.With(("Beacon:Hosting:DrainSeconds", "0.3"), ("AI:ScriptedChunkDelayMilliseconds", "150"));
        var governor = agent.Services.GetRequiredService<RunGovernor>();
        var ct = TestContext.Current.CancellationToken;

        using var response = await agent.CreateClient(DevTokens.Dana).PostAsJsonRunAsync(AGUIWire.UserRun("What is the status of WO-1001?"), ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);

        List<JsonElement> frames = [];
        await foreach (var item in SseParser.Create(body).EnumerateAsync(ct))
        {
            using var document = JsonDocument.Parse(item.Data);
            frames.Add(document.RootElement.Clone());
            if (frames.Count == 1)
            {
                Assert.Equal(1, governor.ActiveRuns);
                governor.BeginDrain(); // SIGTERM arrives while the run streams
            }
        }

        Assert.Equal("RUN_ERROR", frames[^1].GetProperty("type").GetString());
        Assert.Equal(RunGovernor.ServerShutdownCode, frames[^1].GetProperty("code").GetString());
        Assert.Equal(0, governor.ActiveRuns);
    }
}
