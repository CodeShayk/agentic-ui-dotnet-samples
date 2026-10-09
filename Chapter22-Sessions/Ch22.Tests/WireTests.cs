using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Testing;
using Ch22.Tests.Infrastructure;

namespace Ch22.Tests;

/// <summary>What the agent writes, read as raw frames: no protocol field is ever <c>null</c>.</summary>
public sealed class WireTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_frame_writes_null_in_a_run_a_resume_a_branch_or_a_restore()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path);
        using var http = agent.CreateClient();
        List<JsonElement> frames = [];

        var question = Wire.User("u-1", Elena.C33Question);
        var asked = await Wire.RunAsync(http, new RunAgentInput { ThreadId = "t-wire", RunId = "r-1", Messages = [question] }, Ct);
        frames.AddRange(asked);

        var interruptId = asked[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        frames.AddRange(await Wire.RunAsync(http, new RunAgentInput
        {
            ThreadId = "t-wire",
            RunId = "r-2",
            ParentRunId = "r-1",
            Messages = [question],
            Resume = [new AGUIResume { InterruptId = interruptId, Status = ResumeStatus.Resolved, Payload = Elena.Readings }],
        }, Ct));

        var note = Wire.User("u-3", Elena.NoteQuestion);
        frames.AddRange(await Wire.RunAsync(http, new RunAgentInput { ThreadId = "t-wire", RunId = "r-3", ParentRunId = "r-2", Messages = [note] }, Ct));
        var branched = await Wire.RunAsync(http, new RunAgentInput { ThreadId = "t-wire", RunId = "r-4", ParentRunId = "r-2", Messages = [note] }, Ct);
        frames.AddRange(branched);

        var restored = await Wire.GetAsync(http, "/agent/threads/t-wire/events?runId=r-1", Ct);
        frames.AddRange(restored);

        Assert.All(frames, frame => Assert.Empty(AGUIWire.NullProperties(frame)));

        // Every RUN_STARTED declares the version, the restated one included (Chapter 19).
        Assert.Equal(5, frames.Count(f => f.GetProperty("type").GetString() == "RUN_STARTED"));
        Assert.All(frames.Where(f => f.GetProperty("type").GetString() == "RUN_STARTED"),
            started => Assert.Equal(AGUIProtocol.Version, started.GetProperty("protocolVersion").GetString()));

        // The branch restates its history, and the restored question comes back with its interrupt.
        Assert.Equal(["RUN_STARTED", "MESSAGES_SNAPSHOT"], Wire.Types(branched).Take(2));
        Assert.Equal("r-2", branched[0].GetProperty("parentRunId").GetString());
        Assert.Equal(["RUN_STARTED", "MESSAGES_SNAPSHOT", "RUN_FINISHED"], Wire.Types(restored));
        Assert.Equal(interruptId, restored[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString());
    }
}
