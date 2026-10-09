using System.Diagnostics;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Server;
using Beacon.Testing;
using Microsoft.Extensions.AI;

namespace Ch25.Tests;

/// <summary>The shape of a run's trace: which spans exist and how they nest.</summary>
public sealed class TraceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_is_one_trace_from_the_request_through_the_model_and_tools()
    {
        await using var host = TracedAgentHost.Start();

        var events = await AGUIWire.RunAsync(
            host.CreateClient(), "/agent", AGUIWire.UserRun("What is the status of WO-1001?", "thread-trace", "run-trace"), Ct);
        var trace = await host.WaitForTraceAsync("run-trace", Ct);

        var request = Assert.Single(trace, s => s.DisplayName == "POST /agent");
        var run = Assert.Single(trace, s => s.OperationName == "agui.run");
        var orchestrate = Assert.Single(trace, s => s.DisplayName == "orchestrate_tools");
        var tool = Assert.Single(trace, s => s.DisplayName == "execute_tool get_work_order");
        var chats = trace.Where(s => s.DisplayName == "chat").OrderBy(s => s.StartTimeUtc).ToList();

        // ASP.NET Core's request span > the AG-UI run > Microsoft.Extensions.AI's tool loop > model calls and the tool.
        Assert.Equal(ActivityKind.Server, request.Kind);
        Assert.Equal(request.SpanId, run.ParentSpanId);
        Assert.Equal(run.SpanId, orchestrate.ParentSpanId);
        Assert.Equal(2, chats.Count);
        Assert.All(chats, chat => Assert.Equal(orchestrate.SpanId, chat.ParentSpanId));
        Assert.Equal(orchestrate.SpanId, tool.ParentSpanId);
        Assert.True(chats[0].StartTimeUtc <= tool.StartTimeUtc && tool.StartTimeUtc <= chats[1].StartTimeUtc);

        // Which instrumentation produced each span.
        Assert.Equal(AGUIServerInstrumentation.ActivitySourceName, run.Source.Name);
        Assert.All([orchestrate, tool, .. chats], s => Assert.Equal("Experimental.Microsoft.Extensions.AI", s.Source.Name));

        // The run span describes the run the client saw.
        Assert.Equal("thread-trace", run.GetTagItem("agui.thread_id"));
        Assert.Equal("run-trace", run.GetTagItem("agui.run_id"));
        Assert.Equal("success", run.GetTagItem("agui.run.outcome"));
        Assert.Equal(events.Count, Convert.ToInt32(run.GetTagItem("agui.events.count")));

        // Model calls carry the generative AI attributes.
        Assert.All(chats, chat =>
        {
            Assert.Equal("chat", chat.GetTagItem("gen_ai.operation.name"));
            Assert.Equal("beacon-scripted", chat.GetTagItem("gen_ai.response.model"));
            Assert.NotNull(chat.GetTagItem("gen_ai.usage.input_tokens"));
        });
        Assert.Equal("get_work_order", tool.GetTagItem("gen_ai.tool.name"));
    }

    [Fact]
    public async Task A_slow_dependency_shows_up_under_its_tool_as_the_longest_span_of_the_run()
    {
        await using var host = TracedAgentHost.Start(kestrel: true, settings: ("Beacon:FaultArchive:DelayMilliseconds", "600"));

        await AGUIWire.RunAsync(
            host.CreateClient(), "/agent", AGUIWire.UserRun("Show me the fault history for EQ-501.", "thread-slow", "run-slow"), Ct);
        var trace = await host.WaitForTraceAsync("run-slow", Ct);

        var orchestrate = Assert.Single(trace, s => s.DisplayName == "orchestrate_tools");
        var tool = Assert.Single(trace, s => s.DisplayName == "execute_tool get_fault_history");
        var call = Assert.Single(trace, s => s.Kind == ActivityKind.Client && s.Source.Name == "System.Net.Http" && s.ParentSpanId == tool.SpanId);
        var archive = Assert.Single(trace, s => s.DisplayName == "GET /archive/faults/{equipmentId}");

        // The tool's HTTP call carried the trace context, so the archive's server span joins the same trace.
        Assert.Equal(call.SpanId, archive.ParentSpanId);
        Assert.EndsWith("/archive/faults/EQ-501", (string)call.GetTagItem("url.full")!);

        // The time went to the archive, not to the model (Task.Delay can end a timer tick early).
        Assert.True(archive.Duration >= TimeSpan.FromMilliseconds(580));
        var slowest = trace.Where(s => s.ParentSpanId == orchestrate.SpanId).MaxBy(s => s.Duration);
        Assert.Same(tool, slowest);
    }

    [Fact]
    public async Task A_client_trace_continues_on_the_server()
    {
        await using var host = TracedAgentHost.Start(kestrel: true);

        using var source = new ActivitySource("Ch25.Tests.Client");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        var http = host.CreateClient();
        using IChatClient beacon = new AGUIChatClient(new AGUIChatClientOptions(http, new Uri(http.BaseAddress!, "agent").ToString()));

        string? runId = null;
        ActivityTraceId clientTrace;
        using (var ask = source.StartActivity("dana asks"))
        {
            clientTrace = ask!.TraceId;
            await foreach (var update in beacon.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "What is the status of WO-1001?") { MessageId = "user-1" }], cancellationToken: Ct))
            {
                runId ??= (update.RawRepresentation as RunStartedEvent)?.RunId;
            }
        }

        // The W3C traceparent header carried the client's trace into the agent.
        var trace = await host.WaitForTraceAsync(runId!, Ct);
        Assert.All(trace, span => Assert.Equal(clientTrace, span.TraceId));
        Assert.Contains(trace, s => s.OperationName == "agui.run");
    }
}
