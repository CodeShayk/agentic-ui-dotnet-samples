using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Beacon.Testing;
using Ch13.Tests.Infrastructure;

namespace Ch13.Tests;

/// <summary>Ch13.Beacon.Agent in process: predicted state, activity and steps as they go on the wire.</summary>
public sealed class AgentWireTests
{
    [Fact]
    public async Task The_report_is_predicted_while_its_arguments_stream_and_confirmed_after_the_tool_runs()
    {
        using var host = new AgentHost();
        var events = await host.RunAsync();

        var callId = events.OfType<ToolCallStartEvent>().Single(e => e.ToolCallName == "write_service_report").ToolCallId;
        var start = Index(events, e => e is ToolCallStartEvent s && s.ToolCallId == callId);
        var end = Index(events, e => e is ToolCallEndEvent x && x.ToolCallId == callId);
        var result = Index(events, e => e is ToolCallResultEvent r && r.ToolCallId == callId);

        // Between start and end: argument pieces, most followed by the report as it reads so far.
        var streamed = events.Skip(start).Take(end - start).ToList();
        var pieces = streamed.OfType<ToolCallArgsEvent>().Count();
        var predicted = streamed.OfType<StateDeltaEvent>().Count();
        Assert.True(pieces > 40, $"expected the arguments in many pieces, got {pieces}");
        Assert.InRange(predicted, pieces - 10, pieces - 1); // pieces that change nothing visible send nothing

        // MapCall: after TOOL_CALL_END, before the result; only the status is new by then.
        Assert.Equal("""[{"op":"add","path":"/report/status","value":"saving"}]""", Delta(events[end + 1]));
        Assert.True(end + 1 < result);
        // MapResult: right after the result.
        Assert.Equal("""[{"op":"add","path":"/report/status","value":"saved"}]""", Delta(events[result + 1]));
    }

    [Fact]
    public async Task Every_state_delta_applies_and_the_run_ends_with_the_plan_done_and_the_report_saved()
    {
        using var host = new AgentHost();
        var events = await host.RunAsync();

        JsonNode? state = new JsonObject(); // the run carried no state: deltas apply to {}
        foreach (var delta in events.OfType<StateDeltaEvent>())
        {
            var applied = JsonPatch.Apply(state, delta.Delta);
            Assert.True(applied.Succeeded, applied.Error);
            state = applied.Document;
        }

        Assert.All(state!["plan"]!["steps"]!.AsArray(), step => Assert.Equal("done", step!["status"]!.GetValue<string>()));
        Assert.Equal("saved", state["report"]!["status"]!.GetValue<string>());
        Assert.StartsWith("The Contoso Air CA-36X (EQ-501) at Harbor View Dental", state["report"]!["summary"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_activity_starts_with_a_snapshot_and_every_delta_applies_to_it()
    {
        using var host = new AgentHost();
        var events = await host.RunAsync();

        var activity = events.Where(e => e is ActivitySnapshotEvent or ActivityDeltaEvent).ToList();
        var snapshot = Assert.IsType<ActivitySnapshotEvent>(activity[0]); // the baseline comes first
        Assert.Equal(("checks-r-1", "beacon.checks"), (snapshot.MessageId, snapshot.ActivityType));

        JsonNode? content = JsonNode.Parse(snapshot.Content.GetRawText());
        foreach (var delta in activity.Skip(1).Cast<ActivityDeltaEvent>())
        {
            Assert.Equal(("checks-r-1", "beacon.checks"), (delta.MessageId, delta.ActivityType)); // the type is repeated on every delta
            var applied = JsonPatch.Apply(content, delta.Patch);
            Assert.True(applied.Succeeded, applied.Error);
            content = applied.Document;
        }

        Assert.Equal(
            [
                ("Work order WO-1001", "done", "Scheduled, High priority, TECH-1 at 09:00"),
                ("Fault history of EQ-501", "done", "3 faults; latest E5 on 2026-11-14"),
                ("Warranty on EQ-501", "done", "Covered until 2029-05-14"),
                ("Stock of capacitor for CA-36X", "done", "CAP-45-5, $38.50, 24 in stock"),
            ],
            content!["checks"]!.AsArray().Select(c => (
                c!["label"]!.GetValue<string>(), c["status"]!.GetValue<string>(), c["finding"]!.GetValue<string>())));
    }

    [Fact]
    public async Task Steps_open_and_close_in_pairs_and_none_is_open_when_the_run_finishes()
    {
        using var host = new AgentHost();
        var events = await host.RunAsync();

        HashSet<string> open = [];
        List<string> order = [];
        foreach (var evt in events)
        {
            switch (evt)
            {
                case StepStartedEvent started:
                    Assert.True(open.Add(started.StepName), $"'{started.StepName}' opened twice");
                    order.Add(started.StepName);
                    break;
                case StepFinishedEvent finished:
                    Assert.True(open.Remove(finished.StepName), $"'{finished.StepName}' finished without starting");
                    break;
                case RunFinishedEvent:
                    Assert.Empty(open);
                    break;
            }
        }

        Assert.Equal(["planning", "investigating", "reporting"], order);
        // A step brackets its calls: the report's step starts before the report's first event.
        Assert.True(Index(events, e => e is StepStartedEvent { StepName: "reporting" })
            < Index(events, e => e is ToolCallStartEvent { ToolCallName: "write_service_report" }));
    }

    [Fact]
    public async Task The_wire_carries_no_nulls_and_the_same_question_gives_the_same_stream()
    {
        using var first = new AgentHost();
        using var second = new AgentHost();

        var frames = await first.RunRawAsync();
        var again = await second.RunRawAsync();

        Assert.All(frames, frame => Assert.Empty(AGUIWire.NullProperties(frame)));
        Assert.Equal(frames.Select(Comparable), again.Select(Comparable));
    }

    // rawEvent echoes the source update, which carries a creation time; everything else must match.
    private static string Comparable(JsonElement frame)
    {
        var node = JsonNode.Parse(frame.GetRawText())!.AsObject();
        node.Remove("rawEvent");
        return node.ToJsonString();
    }

    private static int Index(IReadOnlyList<BaseEvent> events, Func<BaseEvent, bool> match)
    {
        for (var i = 0; i < events.Count; i++)
        {
            if (match(events[i]))
            {
                return i;
            }
        }

        throw new InvalidOperationException("No such event.");
    }

    private static string Delta(BaseEvent evt) => Assert.IsType<StateDeltaEvent>(evt).Delta.GetRawText();
}
