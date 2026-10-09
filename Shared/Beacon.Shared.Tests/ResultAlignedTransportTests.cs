using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Beacon.Shared.Tests;

/// <summary>
/// <c>ResultAlignedTransport</c> (Chapter 13, now in <c>Beacon.Client.Core</c>): what the agent streams
/// while a tool runs reaches the client on time, and the chat client still sees every call end.
/// </summary>
public sealed class ResultAlignedTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly BaseEvent[] ToolRun =
    [
        new RunStartedEvent { ThreadId = "t-1", RunId = "r-1" },
        new ToolCallStartEvent { ToolCallId = "call-1", ToolCallName = "get_work_order", ParentMessageId = "msg-1" },
        new ToolCallArgsEvent { ToolCallId = "call-1", Delta = "{\"workOrderId\":\"WO-1001\"}" },
        new ToolCallEndEvent { ToolCallId = "call-1" },
        new StateSnapshotEvent { Snapshot = System.Text.Json.JsonDocument.Parse("""{"step":"looking up"}""").RootElement.Clone() },
        new ToolCallResultEvent { MessageId = "call-1", ToolCallId = "call-1", Content = new AGUIContent("{\"status\":\"Scheduled\"}") },
        new RunFinishedEvent { ThreadId = "t-1", RunId = "r-1" },
    ];

    [Fact]
    public async Task Each_TOOL_CALL_END_moves_to_just_before_its_result()
    {
        var transport = new ResultAlignedTransport(new Fixed(ToolRun));

        List<string> types = [];
        await foreach (var evt in transport.SendAsync(new RunAgentInput { ThreadId = "t-1", RunId = "r-1" }, Ct))
        {
            types.Add(evt.Type);
        }

        Assert.Equal(["RUN_STARTED", "TOOL_CALL_START", "TOOL_CALL_ARGS", "STATE_SNAPSHOT", "TOOL_CALL_END", "TOOL_CALL_RESULT", "RUN_FINISHED"], types);
    }

    [Fact]
    public async Task A_call_without_a_result_ends_before_the_run_and_a_broken_stream_still_releases_it()
    {
        BaseEvent[] pending =
        [
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-1" },
            new ToolCallStartEvent { ToolCallId = "call-1", ToolCallName = "open_work_order" },
            new ToolCallEndEvent { ToolCallId = "call-1" },
            new CustomEvent { Name = "note", Value = System.Text.Json.JsonDocument.Parse("1").RootElement.Clone() },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-1" },
        ];

        Assert.Equal(["RUN_STARTED", "TOOL_CALL_START", "CUSTOM", "TOOL_CALL_END", "RUN_FINISHED"], await TypesAsync(pending));
        Assert.Equal(["RUN_STARTED", "TOOL_CALL_START", "CUSTOM", "TOOL_CALL_END"], await TypesAsync(pending[..^1]));
    }

    [Fact]
    public async Task Through_AGUIChatClient_the_state_arrives_before_the_tool_result_only_when_aligned()
    {
        Assert.Equal(["RunStartedEvent", "StateSnapshotEvent", "call", "result", "RunFinishedEvent"], await UpdatesAsync(aligned: true));

        // Without it, AGUIChatClient 1.0.0 holds the snapshot back with the call until the call's result
        // arrives, then releases all three together.
        Assert.Equal(["RunStartedEvent", "call", "StateSnapshotEvent", "result", "RunFinishedEvent"], await UpdatesAsync(aligned: false));
    }

    private static async Task<List<string>> UpdatesAsync(bool aligned)
    {
        IAGUITransport transport = new Fixed(ToolRun);
        using var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = aligned ? new ResultAlignedTransport(transport) : transport });

        List<string> seen = [];
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hi") { MessageId = "user-1" }], cancellationToken: Ct))
        {
            foreach (var content in update.Contents)
            {
                seen.Add(content switch { FunctionCallContent => "call", FunctionResultContent => "result", _ => content.GetType().Name });
            }

            if (update.Contents.Count == 0 && update.RawRepresentation is BaseEvent evt)
            {
                seen.Add(evt.GetType().Name);
            }
        }

        return seen;
    }

    private static async Task<List<string>> TypesAsync(BaseEvent[] events)
    {
        List<string> types = [];
        await foreach (var evt in new ResultAlignedTransport(new Fixed(events)).SendAsync(new RunAgentInput { ThreadId = "t-1", RunId = "r-1" }, Ct))
        {
            types.Add(evt.Type);
        }

        return types;
    }

    private sealed class Fixed(BaseEvent[] events) : IAGUITransport
    {
        public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var evt in events)
            {
                await Task.Yield();
                yield return evt;
            }
        }
    }
}
