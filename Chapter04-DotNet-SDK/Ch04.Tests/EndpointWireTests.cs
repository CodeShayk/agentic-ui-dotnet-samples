using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Testing;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ch04.Tests;

/// <summary>What Ch04.Beacon.Agent puts on the wire, asserted property by property.</summary>
public sealed class EndpointWireTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_scripted_answer_streams_one_well_formed_run()
    {
        await using var agent = new AgentFactory(TestModels.Scripted());

        var events = await AGUIWire.RunAsync(agent.CreateClient(), "/agent", AGUIWire.UserRun("Hello"), Ct);

        Assert.Equal(
            ["RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED"],
            Collapse(AGUIWire.Types(events)));
    }

    [Fact]
    public async Task Wire_json_carries_the_run_ids_and_one_message_id()
    {
        await using var agent = new AgentFactory(TestModels.Scripted());

        var events = await AGUIWire.RunAsync(
            agent.CreateClient(), "/agent", AGUIWire.UserRun("Hello", threadId: "thread-7", runId: "run-7"), Ct);
        var wire = events.Select(AGUIWire.ToWireJson).ToList();

        var started = wire[0];
        Assert.Equal("RUN_STARTED", started.GetProperty("type").GetString());
        Assert.Equal("thread-7", started.GetProperty("threadId").GetString());
        Assert.Equal("run-7", started.GetProperty("runId").GetString());

        var start = wire[1];
        Assert.Equal("TEXT_MESSAGE_START", start.GetProperty("type").GetString());
        Assert.Equal("assistant", start.GetProperty("role").GetString());
        var messageId = start.GetProperty("messageId").GetString();

        var content = wire.Where(e => e.GetProperty("type").GetString() == "TEXT_MESSAGE_CONTENT").ToList();
        Assert.All(content, e => Assert.Equal(messageId, e.GetProperty("messageId").GetString()));
        Assert.Equal(BeaconScripts.Fallback.Text, string.Concat(content.Select(e => e.GetProperty("delta").GetString())));

        var end = wire[^2];
        Assert.Equal("TEXT_MESSAGE_END", end.GetProperty("type").GetString());
        Assert.Equal(messageId, end.GetProperty("messageId").GetString());

        var finished = wire[^1];
        Assert.Equal("RUN_FINISHED", finished.GetProperty("type").GetString());
        Assert.Equal("thread-7", finished.GetProperty("threadId").GetString());
        Assert.Equal("run-7", finished.GetProperty("runId").GetString());
        Assert.Equal("success", finished.GetProperty("outcome").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Unset_optional_fields_are_omitted_from_the_raw_frames()
    {
        await using var agent = new AgentFactory(TestModels.Scripted());

        var frames = await RawFrames.PostAsync(agent.CreateClient(), """
            {"threadId":"thread-1","runId":"run-1","messages":[{"id":"user-1","role":"user","content":"Hello"}]}
            """, Ct);

        // No top-level field of any event is a bare null.
        Assert.All(frames, frame =>
            Assert.All(frame.EnumerateObject(), property =>
                Assert.NotEqual(JsonValueKind.Null, property.Value.ValueKind)));

        Assert.False(frames[0].TryGetProperty("parentRunId", out _));
        Assert.False(frames[0].TryGetProperty("input", out _));
        Assert.False(frames[1].TryGetProperty("name", out _));
        Assert.False(frames[^1].TryGetProperty("result", out _));
        Assert.False(frames[^1].TryGetProperty("usage", out _));
    }

    [Fact]
    public async Task A_failing_model_ends_the_run_with_a_sanitized_RUN_ERROR()
    {
        await using var agent = new AgentFactory(new FailingChatClient());

        var events = await AGUIWire.RunAsync(agent.CreateClient(), "/agent", AGUIWire.UserRun("Hello"), Ct);

        // The open message is abandoned, not closed: RUN_ERROR ends the run where it stands.
        Assert.Equal(
            ["RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "RUN_ERROR"],
            AGUIWire.Types(events));

        var error = AGUIWire.ToWireJson(events[^1]);
        Assert.Equal("StreamingError", error.GetProperty("code").GetString());
        Assert.DoesNotContain(FailingChatClient.SecretDetail, error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.OK)]
    [InlineData("text/event-stream", HttpStatusCode.OK)]
    [InlineData("*/*", HttpStatusCode.OK)]
    [InlineData("application/json", HttpStatusCode.NotAcceptable)]
    [InlineData("application/*", HttpStatusCode.NotAcceptable)]
    [InlineData("application/vnd.ag-ui.event+proto", HttpStatusCode.NotAcceptable)]
    public async Task The_Accept_header_picks_the_format_before_the_model_runs(string? accept, HttpStatusCode expected)
    {
        var model = new RecordedChatClient(RecordedChatClient.TextTurn("msg-1", "Hi."));
        await using var agent = new AgentFactory(model);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = JsonContent.Create(new { threadId = "thread-1", runId = "run-1", messages = Array.Empty<object>() }),
        };
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        using var response = await agent.CreateClient().SendAsync(request, Ct);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        }
        else
        {
            Assert.Empty(model.Calls); // a refused request never reached the model
        }
    }

    [Fact]
    public async Task The_host_resolves_AG_UI_types_first_so_unset_fields_stay_off_the_wire()
    {
        await using var agent = new AgentFactory(TestModels.Scripted());
        var hostOptions = agent.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        // The same two resolvers in the opposite order. Microsoft.Extensions.AI's resolver answers for
        // any type, so whichever resolver comes first decides how AG-UI events are written.
        var reversed = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        reversed.TypeInfoResolverChain.Add(AIJsonUtilities.DefaultOptions.TypeInfoResolver!);
        reversed.TypeInfoResolverChain.Add(AGUIJsonUtilities.DefaultTypeInfoResolver);

        var started = new RunStartedEvent { ThreadId = "thread-1", RunId = "run-1" };

        var right = JsonSerializer.SerializeToElement(started, hostOptions.GetTypeInfo(typeof(RunStartedEvent)));
        var wrong = JsonSerializer.SerializeToElement(started, reversed.GetTypeInfo(typeof(RunStartedEvent)));

        Assert.False(right.TryGetProperty("parentRunId", out _));
        Assert.Equal(JsonValueKind.Null, wrong.GetProperty("parentRunId").ValueKind);
    }

    // The spec answers malformed JSON with a 400. An unknown role is unrecognized material it says to
    // strip with a warning, so the 400 is SDK strictness; reasoning and activity are roles the spec
    // defines, and ToChatRequestContext throws on them, so the glue answers with a 500.
    [Theory]
    [InlineData("""{"threadId":"thread-1","runId":"run-1","messages":[""", HttpStatusCode.BadRequest)]
    [InlineData("""{"threadId":"thread-1","runId":"run-1","messages":[{"id":"m-1","role":"narrator","content":"Once upon a time"}]}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"threadId":"thread-1","runId":"run-1","messages":[{"id":"r-1","role":"reasoning","content":"Short-cycling points to the capacitor."},{"id":"user-1","role":"user","content":"Hello"}]}""", HttpStatusCode.InternalServerError)]
    [InlineData("""{"threadId":"thread-1","runId":"run-1","messages":[{"id":"a-1","role":"activity","activityType":"parts-search","content":{"found":1}},{"id":"user-1","role":"user","content":"Hello"}]}""", HttpStatusCode.InternalServerError)]
    public async Task Input_the_SDK_cannot_read_or_convert_never_reaches_the_model(string body, HttpStatusCode expected)
    {
        var model = new RecordedChatClient(RecordedChatClient.TextTurn("msg-1", "Hi."));
        await using var agent = new AgentFactory(model);

        using var response = await agent.CreateClient().PostAsync(
            "/agent", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(expected, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(model.Calls);
    }

    private static List<string> Collapse(IEnumerable<string> types) =>
        types.Aggregate(new List<string>(), (list, type) =>
        {
            if (list.Count == 0 || list[^1] != type)
            {
                list.Add(type);
            }

            return list;
        });
}
