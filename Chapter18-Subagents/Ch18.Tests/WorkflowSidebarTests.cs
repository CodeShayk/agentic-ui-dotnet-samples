extern alias workflow;

using AGUI.Abstractions;
using AGUI.Server;
using Beacon.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using static Ch18.Tests.Infrastructure.Runs;
using WorkflowProgram = workflow::Ch18.Beacon.Workflow.Program;

namespace Ch18.Tests;

/// <summary>
/// What Microsoft Agent Framework's MapAGUIServer (1.23.0-preview.260928.1) puts on the wire for a concurrent
/// workflow of three agents, pinned so a later preview that changes it fails here first.
/// </summary>
public sealed class WorkflowSidebarTests
{
    [Fact]
    public async Task A_workflow_behind_MapAGUIServer_reports_no_subagents_or_steps_only_author_names()
    {
        await using var host = new WorkflowHost();
        var frames = await AGUIWire.RunRawAsync(host.CreateClient(), "/agent", Turn("t-1", "r-1", Capacitor), Ct);

        Assert.DoesNotContain(frames, f => f.Type().StartsWith("SUBAGENT_", StringComparison.Ordinal) || f.Type().StartsWith("STEP_", StringComparison.Ordinal));
        Assert.DoesNotContain(frames, f => f.TryGetProperty("subagentRunId", out _));

        // Who wrote a message survives only as TEXT_MESSAGE_START's name. Tool calls carry nothing of their own.
        var starts = frames.Where(f => f.Type() == "TEXT_MESSAGE_START").ToList();
        Assert.Equal(["parts-specialist", "scheduling-specialist", "warranty-specialist"], starts.Select(s => s.Str("name")!).Distinct().Order());
        Assert.All(frames.Where(f => f.Type() == "TOOL_CALL_START"), f => Assert.False(f.TryGetProperty("name", out _)));

        // The SDK's conversion keeps one text message open at a time, so interleaved agents close and reopen theirs.
        Assert.True(starts.Count > starts.Select(s => s.Str("messageId")).Distinct().Count());
        Assert.Equal("success", frames[^1].GetProperty("outcome").Str("type"));
    }

    [Fact]
    public async Task MapAGUIServer_uses_AGUIStreamOptions_from_dependency_injection()
    {
        await using var host = new WorkflowHost(services => services.Configure<AGUIStreamOptions>(options =>
            options.MapResult("get_schedule", _ => [new CustomEvent { Name = "schedule_checked" }])));

        var frames = await AGUIWire.RunRawAsync(host.CreateClient(), "/agent", Turn("t-1", "r-1", Capacitor), Ct);

        var result = frames.Select((f, i) => (f, i)).Single(x => x.f.Type() == "TOOL_CALL_RESULT" && x.f.Str("messageId") is { } id
            && frames.Any(s => s.Type() == "TOOL_CALL_START" && s.Str("toolCallId") == id && s.Str("toolCallName") == "get_schedule")).i;
        Assert.Equal(("CUSTOM", "schedule_checked"), (frames[result + 1].Type(), frames[result + 1].Str("name")));
    }

    private sealed class WorkflowHost(Action<IServiceCollection>? configure = null) : WebApplicationFactory<WorkflowProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            if (configure is not null)
            {
                builder.ConfigureTestServices(configure);
            }
        }
    }
}
