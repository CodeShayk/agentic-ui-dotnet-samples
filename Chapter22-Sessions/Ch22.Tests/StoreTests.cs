using AGUI.Abstractions;
using Ch22.Beacon.Agent.Conversations;
using Ch22.Beacon.Agent.Runs;
using Ch22.Tests.Infrastructure;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Ch22.Tests;

/// <summary>The file store on its own: crashes, torn writes, retention and keys.</summary>
public sealed class StoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_cut_off_by_a_crash_is_rebuilt_from_the_event_log_and_restated_as_RUN_ERROR()
    {
        using var folder = new TempFolder();
        var question = new AGUIUserMessage { Id = "u-1", Content = Elena.C33Question };

        // A run starts and streams two events; then the process dies before the run is recorded.
        using (var store = Store(folder.Path))
        {
            var conversation = await store.GetOrCreateAsync("t-crash", Ct);
            using var lease = conversation.TryBeginRun();
            var log = await store.BeginRunAsync(conversation, new RunRecord { RunId = "r-1", StartedAt = DateTimeOffset.UtcNow, Input = [question] }, Ct);
            await log.AppendAsync(new RunStartedEvent
            {
                ThreadId = "t-crash",
                RunId = "r-1",
                Input = new RunAgentInput { ThreadId = "t-crash", RunId = "r-1", Messages = [question] },
            }, Ct);
            await log.AppendAsync(new TextMessageStartEvent { MessageId = "msg-1", Role = AGUIRoles.Assistant }, Ct);
            await log.AppendAsync(new TextMessageContentEvent { MessageId = "msg-1", Delta = "Let me check EQ-701's fault history first." }, Ct);
            await log.DisposeAsync();
        }

        // The next process loads the thread and records what the client was shown. The process died under the
        // run, so it didn't end cleanly: broken, not cancelled.
        using (var restarted = Store(folder.Path))
        {
            var reloaded = await restarted.FindAsync("t-crash", Ct);

            var run = Assert.Single(reloaded!.Runs);
            Assert.Equal(RunEnding.Broken, run.Ending);
            Assert.Equal("u-1", Assert.Single(run.Input).Id);
            Assert.Equal("Let me check EQ-701's fault history first.", Assert.IsType<AGUIAssistantMessage>(Assert.Single(run.Output)).Content);
            Assert.Equal("r-1", reloaded.Header.HeadRunId);
            Assert.Null(reloaded.Header.OpenRunId);
        }

        // Restored through the thread API, the run ends with RUN_ERROR, and RUN_STARTED declares the version.
        await using var agent = new AgentHost(folder.Path);
        using var http = agent.CreateClient();
        var frames = await Wire.GetAsync(http, "/agent/threads/t-crash/events", Ct);

        Assert.Equal(["RUN_STARTED", "MESSAGES_SNAPSHOT", "RUN_ERROR"], Wire.Types(frames));
        Assert.Equal("1.0", frames[0].GetProperty("protocolVersion").GetString());
        Assert.Equal(Restatements.BrokenRunCode, frames[^1].GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_torn_last_line_is_skipped_and_the_rest_loads()
    {
        using var folder = new TempFolder();
        await using (var agent = new AgentHost(folder.Path))
        {
            using var http = agent.CreateClient();
            await Wire.RunAsync(http, new RunAgentInput { ThreadId = "t-torn", RunId = "r-1", Messages = [Wire.User("u-1", "What is the status of WO-1005?")] }, Ct);
        }

        using var store = Store(folder.Path);
        await File.AppendAllTextAsync(Path.Combine(store.FolderFor("t-torn"), "runs.jsonl"), """{"runId":"r-2","startedAt":"2026-11""", Ct);

        var reloaded = await store.FindAsync("t-torn", Ct);
        Assert.Equal("r-1", Assert.Single(reloaded!.Runs).RunId);
    }

    [Fact]
    public async Task Retention_removes_idle_threads_and_old_event_logs()
    {
        using var folder = new TempFolder();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using (var agent = new AgentHost(folder.Path, time: clock))
        {
            using var http = agent.CreateClient();
            await Wire.RunAsync(http, new RunAgentInput { ThreadId = "t-old", RunId = "r-1", Messages = [Wire.User("u-1", "What is the status of WO-1005?")] }, Ct);
        }

        using var store = Store(folder.Path, clock);
        var threadFolder = store.FolderFor("t-old");

        clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal((0, 1), await store.ApplyRetentionAsync(Ct));
        Assert.False(File.Exists(Path.Combine(threadFolder, "events.jsonl")));
        Assert.NotNull(await store.FindAsync("t-old", Ct)); // the conversation itself stays

        clock.Advance(TimeSpan.FromDays(60));
        Assert.Equal((1, 0), await store.ApplyRetentionAsync(Ct));
        Assert.False(Directory.Exists(threadFolder));
    }

    [Fact]
    public void Thread_ids_become_hashed_folder_names_never_paths()
    {
        using var folder = new TempFolder();
        using var store = Store(folder.Path);

        var name = Path.GetFileName(store.FolderFor("t:1005"));
        Assert.Matches("^[0-9a-f]{32}$", name);
        Assert.Equal(store.Root, Path.GetDirectoryName(store.FolderFor("../../t:1005")));
    }

    internal static FileConversationStore Store(string folder, TimeProvider? time = null) => new(
        Options.Create(new ConversationStoreOptions { Directory = folder }),
        new TestEnvironment(folder),
        time ?? TimeProvider.System,
        NullLogger<FileConversationStore>.Instance);

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Ch22.Tests";

        public string ContentRootPath { get; set; } = root;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
