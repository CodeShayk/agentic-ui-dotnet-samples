using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using AGUI.Abstractions;
using AGUI.Server;
using Beacon.Testing;
using Ch09.Beacon.Agent;
using Ch09.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Chat;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Ch09.Tests;

/// <summary>
/// Streaming argument previews: the extractor registered with MapStreamingToolCallArguments, for the
/// scripted model and for the OpenAI provider, all the way from the provider's chunks to the wire.
/// </summary>
public sealed class ArgumentStreamingTests
{
    [Fact]
    public void The_extractor_reads_both_providers_pieces_and_nothing_else()
    {
        var scripted = ToolArgumentFragments.Extract(new ChatResponseUpdate
        {
            RawRepresentation = new ScriptedArgumentFragment(0, "call-1", "get_work_order", "{\"work"),
        });

        var chunk = OpenAIChatModelFactory.StreamingChatCompletionUpdate(toolCallUpdates:
        [
            OpenAIChatModelFactory.StreamingChatToolCallUpdate(0, "call_a", ChatToolCallKind.Function, "check_warranty", BinaryData.FromString("{\"equip")),
            OpenAIChatModelFactory.StreamingChatToolCallUpdate(1, "call_b", ChatToolCallKind.Function, "search_parts", BinaryData.FromString("{\"query")),
        ]);
        var openAI = ToolArgumentFragments.Extract(new ChatResponseUpdate { RawRepresentation = chunk });

        var piece = Assert.Single(scripted!);
        Assert.Equal((0, "call-1", "get_work_order", "{\"work"), (piece.Index, piece.ToolCallId, piece.FunctionName, piece.ArgumentsDelta));
        Assert.Equal([(0, "call_a", "{\"equip"), (1, "call_b", "{\"query")], openAI!.Select(p => (p.Index, p.ToolCallId, p.ArgumentsDelta)));

        Assert.Null(ToolArgumentFragments.Extract(new ChatResponseUpdate(ChatRole.Assistant, "text")));
        Assert.Null(ToolArgumentFragments.Extract(new ChatResponseUpdate { RawRepresentation = OpenAIChatModelFactory.StreamingChatCompletionUpdate() }));
    }

    [Fact]
    public async Task OpenAI_chunks_become_one_args_event_each()
    {
        // A fake OpenAI endpoint: the first request answers with a tool call streamed in four chunks,
        // the second (after the tool ran) with text. The real OpenAI SDK and Microsoft.Extensions.AI's
        // OpenAI client parse it; only the network is fake.
        var openAI = new FakeOpenAI(
            ToolCallChunks("call_wo", "get_work_order", ["{\"wor", "kOrder", "Id\":\"WO", "-1001\"}"]),
            TextChunks("WO-1001 is scheduled."));

        await using var agent = new AgentHost(
            new Dictionary<string, string?> { ["AI:Provider"] = "OpenAI" },
            services => services.AddKeyedSingleton<IChatClient>(Ch09.Beacon.Agent.Program.ModelKey, (_, _) => openAI.CreateChatClient()));

        var events = await agent.RunAsync("What's the status of WO-1001?");

        var args = events.OfType<ToolCallArgsEvent>().ToList();
        Assert.Equal(["{\"wor", "kOrder", "Id\":\"WO", "-1001\"}"], args.Select(a => a.Delta));
        Assert.All(args, a => Assert.Equal("call_wo", a.ToolCallId));

        Assert.Equal(
            ["RUN_STARTED", "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_ARGS", "TOOL_CALL_ARGS", "TOOL_CALL_ARGS", "TOOL_CALL_END", "TOOL_CALL_RESULT"],
            AGUIWire.Types(events).Take(8));
        Assert.Contains("WO-1001", events.OfType<ToolCallResultEvent>().Single().Content.ToString());
        Assert.Equal(2, openAI.Requests);
    }

    [Fact]
    public async Task Without_an_extractor_the_same_chunks_arrive_as_one_args_event()
    {
        var openAI = new FakeOpenAI(ToolCallChunks("call_wo", "get_work_order", ["{\"wor", "kOrder", "Id\":\"WO", "-1001\"}"]));
        var updates = openAI.CreateChatClient().GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "status of WO-1001")], cancellationToken: TestContext.Current.CancellationToken);

        var json = new System.Text.Json.JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
        var context = new RunAgentInput { ThreadId = "t", RunId = "r" }.ToChatRequestContext(json, new AGUIStreamOptions());
        List<BaseEvent> events = [];
        await foreach (var evt in updates.AsAGUIEventStreamAsync(context, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var args = Assert.Single(events.OfType<ToolCallArgsEvent>());
        Assert.Equal("{\"workOrderId\":\"WO-1001\"}", args.Delta);
    }

    private static string ToolCallChunks(string callId, string name, string[] pieces)
    {
        var body = new StringBuilder();
        body.Append(Chunk($$$"""{"role":"assistant","content":null,"tool_calls":[{"index":0,"id":"{{{callId}}}","type":"function","function":{"name":"{{{name}}}","arguments":""}}]}"""));
        foreach (var piece in pieces)
        {
            var escaped = System.Text.Json.JsonSerializer.Serialize(piece, OpenAIJson.Default.String);
            body.Append(Chunk($$$"""{"tool_calls":[{"index":0,"function":{"arguments":{{{escaped}}}}}]}"""));
        }

        body.Append(Chunk("{}", "\"tool_calls\""));
        return body.Append("data: [DONE]\n\n").ToString();
    }

    private static string TextChunks(string text) =>
        Chunk("""{"role":"assistant","content":""}""") +
        Chunk($$$"""{"content":{{{System.Text.Json.JsonSerializer.Serialize(text, OpenAIJson.Default.String)}}}}""") +
        Chunk("{}", "\"stop\"") +
        "data: [DONE]\n\n";

    private static string Chunk(string delta, string finishReason = "null") =>
        $$"""data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1793000000,"model":"gpt-test","choices":[{"index":0,"delta":{{delta}},"finish_reason":{{finishReason}}}]}""" + "\n\n";

    /// <summary>Answers each chat-completions request with the next canned SSE body.</summary>
    private sealed class FakeOpenAI(params string[] responses) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public IChatClient CreateChatClient() =>
            new ChatClient(
                    "gpt-test",
                    new ApiKeyCredential("test-key"),
                    new OpenAIClientOptions
                    {
                        Endpoint = new Uri("http://openai.test/v1"),
                        Transport = new HttpClientPipelineTransport(new HttpClient(this)),
                    })
                .AsIChatClient();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _requests) - 1;
            var body = index < responses.Length ? responses[index] : throw new InvalidOperationException($"No response for request {index + 1}.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class OpenAIJson : System.Text.Json.Serialization.JsonSerializerContext;
