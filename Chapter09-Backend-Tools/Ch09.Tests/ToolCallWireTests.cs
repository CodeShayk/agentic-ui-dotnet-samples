using System.Diagnostics;
using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Testing;
using Ch09.Tests.Infrastructure;

namespace Ch09.Tests;

/// <summary>
/// The tool-call events Ch09.Beacon.Agent puts on the wire: start, arguments, end and result for each
/// call, in that order, matched by toolCallId, including three calls from one model turn.
/// </summary>
public sealed class ToolCallWireTests
{
    [Fact]
    public async Task A_backend_call_streams_start_args_and_end_then_its_result()
    {
        await using var agent = new AgentHost();
        var events = await agent.RunAsync("What's the status of WO-1001?");

        var types = AGUIWire.Types(events).ToList();
        Assert.Equal("RUN_STARTED", types[0]);
        Assert.Equal("RUN_FINISHED", types[^1]);

        var call = Assert.Single(Calls(events));
        Assert.Equal("get_work_order", call.Name);
        Assert.True(call.Args.Count > 1, "the arguments should arrive in several pieces");
        Assert.True(call.Start < call.Args[0] && call.Args[^1] < call.End && call.End < call.Result);

        // The pieces concatenate into the arguments, and the result is the tool's JSON.
        Assert.Equal("{\"workOrderId\":\"WO-1001\"}", call.ArgumentText);
        using var result = JsonDocument.Parse(call.ResultContent);
        Assert.Equal("WO-1001", result.RootElement.GetProperty("workOrder").GetProperty("id").GetString());

        // The answer comes after the result, as a message of its own.
        Assert.True(types.IndexOf("TEXT_MESSAGE_START") > call.Result);
    }

    [Fact]
    public async Task Three_calls_from_one_model_turn_each_open_close_and_answer_in_order()
    {
        await using var agent = new AgentHost();
        var events = await agent.RunAsync("Prepare WO-1001 for Priya");

        var calls = Calls(events);
        Assert.Equal(["get_work_order", "search_parts", "check_warranty"], calls.Select(c => c.Name));
        Assert.Equal(3, calls.Select(c => c.Id).Distinct().Count());

        foreach (var call in calls)
        {
            Assert.True(call.Start < call.Args[0], $"{call.Id}: arguments before start");
            Assert.True(call.Args[^1] < call.End, $"{call.Id}: arguments after end");
            Assert.True(call.End < call.Result, $"{call.Id}: result before end");
        }

        // One assistant message carries all three calls.
        Assert.Single(calls.Select(c => c.ParentMessageId).Distinct());

        // Each result names its own call and mints its own tool message.
        Assert.Equal(calls.Select(c => c.Id), calls.Select(c => c.ResultFor));
        Assert.Equal(3, calls.Select(c => c.ResultMessageId).Distinct().Count());

        // Exactly one terminal event, and it's last.
        Assert.Single(events.OfType<RunFinishedEvent>());
        Assert.IsType<RunFinishedEvent>(events[^1]);
        Assert.Empty(events.OfType<RunErrorEvent>());
    }

    [Fact]
    public async Task No_frame_writes_null_for_an_optional_field()
    {
        await using var agent = new AgentHost();
        var frames = await agent.RunRawAsync("Prepare WO-1001 for Priya");

        Assert.All(frames, frame => Assert.Empty(AGUIWire.NullProperties(frame)));
    }

    [Fact]
    public async Task Without_argument_streaming_each_call_has_one_args_event()
    {
        await using var agent = new AgentHost(new Dictionary<string, string?> { ["Beacon:StreamToolArguments"] = "false" });
        var events = await agent.RunAsync("Prepare WO-1001 for Priya");

        var calls = Calls(events);
        Assert.Equal(3, calls.Count);
        Assert.All(calls, call => Assert.Single(call.Args));
        Assert.Equal("{\"equipmentId\":\"EQ-501\"}", calls[2].ArgumentText);
    }

    [Fact]
    public async Task Calls_from_one_turn_run_side_by_side()
    {
        // Three lookups of 600 ms each: one after another they'd take 1.8 s.
        await using var agent = new AgentHost(new Dictionary<string, string?>
        {
            ["BackOffice:LatencyMilliseconds:get_work_order"] = "600",
            ["BackOffice:LatencyMilliseconds:search_parts"] = "600",
            ["BackOffice:LatencyMilliseconds:check_warranty"] = "600",
        });
        await agent.RunAsync("What's the status of WO-1001?", runId: "warm-up");

        var clock = Stopwatch.StartNew();
        var events = await agent.RunAsync("Prepare WO-1001 for Priya");
        clock.Stop();

        Assert.Equal(3, Calls(events).Count);
        // About one lookup's time (the timer may fire a few milliseconds early), well under three.
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(550), TimeSpan.FromMilliseconds(1500));
    }

    /// <summary>One call's events by position in the stream.</summary>
    internal sealed record CallTrace(string Id, string Name, string? ParentMessageId, int Start, List<int> Args, int End, int Result)
    {
        public string ArgumentText { get; init; } = string.Empty;

        public string ResultContent { get; init; } = string.Empty;

        public string? ResultFor { get; init; }

        public string? ResultMessageId { get; init; }
    }

    internal static List<CallTrace> Calls(IReadOnlyList<BaseEvent> events)
    {
        List<CallTrace> calls = [];
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is not ToolCallStartEvent start)
            {
                continue;
            }

            var args = Indexes<ToolCallArgsEvent>(events, e => e.ToolCallId == start.ToolCallId);
            var end = Indexes<ToolCallEndEvent>(events, e => e.ToolCallId == start.ToolCallId).Single();
            var result = Indexes<ToolCallResultEvent>(events, e => e.ToolCallId == start.ToolCallId).Single();
            var resultEvent = (ToolCallResultEvent)events[result];

            calls.Add(new CallTrace(start.ToolCallId, start.ToolCallName, start.ParentMessageId, i, args, end, result)
            {
                ArgumentText = string.Concat(args.Select(a => ((ToolCallArgsEvent)events[a]).Delta)),
                ResultContent = resultEvent.Content.ToString(),
                ResultFor = resultEvent.ToolCallId,
                ResultMessageId = resultEvent.MessageId,
            });
        }

        return calls;
    }

    private static List<int> Indexes<T>(IReadOnlyList<BaseEvent> events, Func<T, bool> match) where T : BaseEvent =>
        [.. events.Select((e, i) => (e, i)).Where(x => x.e is T t && match(t)).Select(x => x.i)];
}
