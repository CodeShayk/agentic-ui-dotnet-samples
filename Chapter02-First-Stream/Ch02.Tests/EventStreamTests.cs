using System.Net;
using System.Text.Json;
using Beacon.AI;

namespace Ch02.Tests;

/// <summary>The shape of the stream: order, IDs, omitted fields and SSE framing.</summary>
public sealed class EventStreamTests
{
    private static ScriptedChatClient Scripted() =>
        new(options: new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero });

    [Fact]
    public async Task A_run_is_started_then_one_message_streams_then_the_run_finishes()
    {
        await using var agent = new TestAgent(Scripted());

        var events = await Wire.RunAsync(agent.CreateClient(), Wire.UserRun("Hello"), TestContext.Current.CancellationToken);

        var types = Wire.Types(events);
        Assert.True(types.Length >= 5, "Expected at least one TEXT_MESSAGE_CONTENT.");
        Assert.Equal("RUN_STARTED", types[0]);
        Assert.Equal("TEXT_MESSAGE_START", types[1]);
        Assert.All(types[2..^2], type => Assert.Equal("TEXT_MESSAGE_CONTENT", type));
        Assert.Equal("TEXT_MESSAGE_END", types[^2]);
        Assert.Equal("RUN_FINISHED", types[^1]);

        // The deltas concatenate to exactly what the model said.
        Assert.Equal(BeaconScripts.Fallback.Text, Wire.Text(events));
    }

    [Fact]
    public async Task IDs_tie_related_events_together()
    {
        await using var agent = new TestAgent(Scripted());

        var events = await Wire.RunAsync(
            agent.CreateClient(), Wire.UserRun("Hello", threadId: "thread-7", runId: "run-42"), TestContext.Current.CancellationToken);

        var started = events[0];
        var finished = events[^1];
        Assert.Equal("thread-7", started.GetProperty("threadId").GetString());
        Assert.Equal("run-42", started.GetProperty("runId").GetString());
        Assert.Equal("thread-7", finished.GetProperty("threadId").GetString());
        Assert.Equal("run-42", finished.GetProperty("runId").GetString());

        var messageId = events[1].GetProperty("messageId").GetString();
        Assert.StartsWith("msg-", messageId);
        Assert.All(events.Skip(1).SkipLast(1), e => Assert.Equal(messageId, e.GetProperty("messageId").GetString()));
    }

    [Fact]
    public async Task Optional_fields_without_a_value_are_omitted_not_null()
    {
        await using var agent = new TestAgent(Scripted());

        var events = await Wire.RunAsync(agent.CreateClient(), Wire.UserRun("Hello"), TestContext.Current.CancellationToken);

        var started = events[0];
        Assert.Equal("1.0", started.GetProperty("protocolVersion").GetString());
        Assert.False(started.TryGetProperty("parentRunId", out _));
        Assert.Equal("assistant", events[1].GetProperty("role").GetString());

        foreach (var evt in events)
        {
            Assert.All(evt.EnumerateObject(), property => Assert.NotEqual(JsonValueKind.Null, property.Value.ValueKind));
        }
    }

    [Fact]
    public async Task Each_frame_is_one_data_line_holding_one_JSON_event_with_LF_line_endings()
    {
        await using var agent = new TestAgent(Scripted());
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await Wire.PostAsync(agent.CreateClient(), Wire.UserRun("Hello"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.DoesNotContain('\r', body);
        var frames = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(frames, frame =>
        {
            Assert.StartsWith("data: ", frame);
            Assert.DoesNotContain('\n', frame);
            using var document = JsonDocument.Parse(frame["data: ".Length..]);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        });
    }

    [Fact]
    public async Task A_full_spec_input_is_accepted_and_members_the_server_does_not_use_are_ignored()
    {
        // spec/1.0/fixtures/RunAgentInput/valid/full.json from the AG-UI repository (MIT License).
        const string fullInput =
            """
            {
              "threadId": "t1", "runId": "r2", "parentRunId": "r1",
              "messages": [
                { "id": "1", "role": "developer", "content": "be brief" },
                { "id": "2", "role": "system", "content": "you are an agent" },
                { "id": "3", "role": "user", "content": "hello" },
                { "id": "4", "role": "assistant", "content": "hi",
                  "toolCalls": [ { "id": "c1", "type": "function", "function": { "name": "search", "arguments": "{\"q\":\"x\"}" } } ] },
                { "id": "5", "role": "tool", "content": "3 results", "toolCallId": "c1" },
                { "id": "6", "role": "activity", "activityType": "search", "content": { "hits": 3 } },
                { "id": "7", "role": "reasoning", "content": "weighing options" }
              ],
              "tools": [ { "name": "search", "description": "Searches", "parameters": {} } ],
              "context": [ { "description": "locale", "value": "en-GB" } ],
              "resume": [ { "interruptId": "i1", "status": "resolved", "payload": true } ]
            }
            """;
        var model = new RecordingChatClient("Hello again.");
        await using var agent = new TestAgent(model);

        var events = await Wire.RunAsync(agent.CreateClient(), fullInput, TestContext.Current.CancellationToken);

        Assert.Equal(["RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED"], Wire.Types(events));
        Assert.Equal("r1", events[0].GetProperty("parentRunId").GetString());

        // The model saw the server's own instructions, then only the user and assistant turns.
        var request = Assert.Single(model.Requests);
        Assert.Equal(["system", "user", "assistant"], request.Select(m => m.Role.Value));
        Assert.Equal(BeaconInstructions.Default, request[0].Text);
        Assert.Equal(["hello", "hi"], request.Skip(1).Select(m => m.Text));
    }

    [Fact]
    public async Task Input_without_messages_is_rejected_before_any_stream_opens()
    {
        await using var agent = new TestAgent(Scripted());

        using var response = await Wire.PostAsync(
            agent.CreateClient(), """{"threadId":"t1","runId":"r1"}""", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }
}
