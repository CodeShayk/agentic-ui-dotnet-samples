using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Testing;
using Ch22.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using AgentFrameworkProgram = Ch22.Beacon.AgentFramework.Program;

namespace Ch22.Tests;

/// <summary>The sidebar: Agent Framework sessions keyed by thread ID, kept in files across a restart.</summary>
public sealed class AgentFrameworkSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_file_session_store_keeps_a_thread_across_a_restart()
    {
        using var folder = new TempFolder();

        await using (var first = new AgentFrameworkHost(folder.Path))
        {
            using var http = first.CreateClient();
            await AGUIWire.RunAsync(http, "/agent", AGUIWire.UserRun("What is the status of WO-1005?", "t-af", "r-1"), Ct);
        }

        Assert.Single(Directory.GetFiles(folder.Path, "*.json"));

        // A new process. With a session store the client sends only its new message, and the session supplies the rest.
        await using var restarted = new AgentFrameworkHost(folder.Path);
        using var client = restarted.CreateClient();
        var input = AGUIWire.UserRun("Is EQ-701 under warranty?", "t-af", "r-2");
        input.Messages[0].Id = "user-2";
        await AGUIWire.RunAsync(client, "/agent", input, Ct);

        var seen = restarted.Model.RunCalls[0].Messages;
        Assert.Equal(["What is the status of WO-1005?", "Is EQ-701 under warranty?"], seen.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
    }

    [Fact]
    public async Task Each_run_opens_with_one_RUN_STARTED_that_declares_AG_UI_1_0()
    {
        using var folder = new TempFolder();
        await using var host = new AgentFrameworkHost(folder.Path);

        // A run that calls a tool: function invocation calls the model twice, and the run still opens once.
        var frames = await AGUIWire.RunRawAsync(host.CreateClient(), "/agent", AGUIWire.UserRun("What is the status of WO-1005?", "t-af", "r-1"), Ct);

        var started = Assert.Single(frames, f => f.GetProperty("type").GetString() == "RUN_STARTED");
        Assert.Equal("RUN_STARTED", frames[0].GetProperty("type").GetString());
        Assert.Equal(("t-af", "r-1", "1.0"), (started.GetProperty("threadId").GetString(),
            started.GetProperty("runId").GetString(), started.GetProperty("protocolVersion").GetString()));
        Assert.Contains(frames, f => f.GetProperty("type").GetString() == "TOOL_CALL_RESULT");
    }

    private sealed class AgentFrameworkHost(string folder) : WebApplicationFactory<AgentFrameworkProgram>
    {
        public ModelSpy Model { get; } = new(new ScriptedChatClient(BeaconScripts.Default, new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero }));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Beacon:Sessions:Directory", folder);
            builder.ConfigureTestServices(services => services.AddKeyedSingleton<IChatClient>(AgentFrameworkProgram.ModelKey, Model));
        }
    }
}
