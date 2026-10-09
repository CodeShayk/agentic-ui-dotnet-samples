using System.Net;
using System.Text.Json;
using Beacon.Testing;
using Ch18.Tests.Infrastructure;
using Microsoft.Extensions.AI;

using static Ch18.Tests.Infrastructure.Runs;

namespace Ch18.Tests;

/// <summary>The coordinator's stream as it appears on the wire, read from the raw SSE frames.</summary>
public sealed class SubagentWireTests
{
    private static readonly string[] Specialists = ["parts-specialist", "scheduling-specialist", "warranty-specialist"];

    // The events that can carry attribution and are about one message or tool call.
    private static readonly HashSet<string> EntityEvents =
    [
        "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END",
        "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "TOOL_CALL_RESULT",
    ];

    [Fact]
    public async Task Each_invocation_is_announced_before_its_events_and_closed_once_before_the_run_finishes()
    {
        await using var host = new AgentHost();
        var frames = await RawAsync(host.Client(), Turn("t-1", "r-1", Capacitor));

        Assert.Equal(Specialists, frames.Where(f => f.Type() == "SUBAGENT_STARTED").Select(f => f.Str("name")!).Order());
        var finished = frames.Select((f, i) => (f, i)).Single(x => x.f.Type() == "RUN_FINISHED").i;

        foreach (var started in frames.Where(f => f.Type() == "SUBAGENT_STARTED"))
        {
            var id = started.Owner()!;
            var announcedAt = IndexOf(frames, f => f.Type() == "SUBAGENT_STARTED" && f.Owner() == id);
            var attributed = Indexes(frames, f => EntityEvents.Contains(f.Type()) && f.Owner() == id);
            var terminals = Indexes(frames, f => f.Type() is "SUBAGENT_FINISHED" or "SUBAGENT_ERROR" && f.Owner() == id);

            Assert.NotEmpty(attributed);
            Assert.True(announcedAt < attributed.Min(), $"{id} was attributed before it was announced.");
            var terminal = Assert.Single(terminals);
            Assert.True(attributed.Max() < terminal, $"{id} produced events after it closed.");
            Assert.True(terminal < finished, $"{id} closed after RUN_FINISHED.");
            Assert.Equal("SUBAGENT_FINISHED", frames[terminal].Type());
            Assert.False(frames[terminal].TryGetProperty("outcome", out _)); // absent means success
        }

        Assert.Equal("success", frames[^1].GetProperty("outcome").Str("type"));
    }

    [Fact]
    public async Task Specialist_events_are_attributed_to_their_invocation_and_the_coordinators_are_not()
    {
        await using var host = new AgentHost();
        var frames = await RawAsync(host.Client(), Turn("t-1", "r-1", Capacitor));
        var invocations = frames.Where(f => f.Type() == "SUBAGENT_STARTED").Select(f => f.Owner()!).ToHashSet();

        // Every message opened by a specialist is continued and closed under the same tag, and every tool
        // call hangs off a message of the same owner.
        Dictionary<string, string?> messageOwners = [];
        foreach (var frame in frames)
        {
            switch (frame.Type())
            {
                case "TEXT_MESSAGE_START":
                    messageOwners[frame.Str("messageId")!] = frame.Owner();
                    break;
                case "TEXT_MESSAGE_CONTENT" or "TEXT_MESSAGE_END" when frame.Owner() is { } owner:
                    Assert.Equal(messageOwners[frame.Str("messageId")!], owner);
                    break;
                case "TOOL_CALL_START":
                    Assert.Contains(frame.Owner()!, invocations);
                    Assert.Equal(messageOwners[frame.Str("parentMessageId")!], frame.Owner());
                    break;
                case "TOOL_CALL_RESULT":
                    Assert.Contains(frame.Owner()!, invocations); // each specialist ran its own tools
                    break;
            }
        }

        // The coordinator's own answer, its steps and the run events carry no tag at all, never a null one.
        var answer = frames.Where(f => f.Type() == "TEXT_MESSAGE_START" && f.Owner() is null).ToList();
        Assert.Single(answer);
        Assert.All(frames.Where(f => f.Type().StartsWith("STEP_", StringComparison.Ordinal) || f.Type().StartsWith("RUN_", StringComparison.Ordinal)),
            f => Assert.False(f.TryGetProperty("subagentRunId", out _)));
        Assert.Contains("**Parts specialist:** CAP-45-5", TextOf(frames, owner: null), StringComparison.Ordinal);
        Assert.StartsWith("Searching stock for a capacitor", TextOf(frames, InvocationOf(frames, "parts-specialist")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_specialists_run_at_the_same_time()
    {
        // Each specialist's lookup waits for the other two to start theirs. Run one after another, the first
        // would wait alone until it gave up.
        System.Collections.Concurrent.ConcurrentBag<bool> met = [];
        await using var host = new AgentHost(TestStore.Rendezvous(met));

        var frames = await RawAsync(host.Client(), Turn("t-1", "r-1", Capacitor));

        Assert.Equal([true, true, true], met);
        var lastStart = Indexes(frames, f => f.Type() == "SUBAGENT_STARTED").Max();
        var firstEnd = Indexes(frames, f => f.Type() is "SUBAGENT_FINISHED" or "SUBAGENT_ERROR").Min();
        Assert.True(lastStart < firstEnd, "Every invocation should be open before the first one closes.");
    }

    [Fact]
    public async Task The_coordinators_steps_bracket_its_phases()
    {
        await using var host = new AgentHost();
        var frames = await RawAsync(host.Client(), Turn("t-1", "r-1", Capacitor));

        var steps = frames.Where(f => f.Type().StartsWith("STEP_", StringComparison.Ordinal))
            .Select(f => $"{f.Type()} {f.Str("stepName")}").ToList();
        Assert.Equal(
            ["STEP_STARTED planning", "STEP_FINISHED planning", "STEP_STARTED consulting", "STEP_FINISHED consulting", "STEP_STARTED answering", "STEP_FINISHED answering"],
            steps);

        // Consulting opens before the first invocation and closes after the last one.
        Assert.True(IndexOf(frames, f => f.Str("stepName") == "consulting") < IndexOf(frames, f => f.Type() == "SUBAGENT_STARTED"));
        Assert.True(Indexes(frames, f => f.Type() == "SUBAGENT_FINISHED").Max()
            < IndexOf(frames, f => f.Type() == "STEP_FINISHED" && f.Str("stepName") == "consulting"));
    }

    [Fact]
    public async Task A_specialist_that_times_out_reports_SUBAGENT_ERROR_and_the_run_still_finishes()
    {
        // EQ-701's warranty provider is down (appsettings.json), and the test host allows each specialist one second.
        await using var host = new AgentHost();
        var frames = await RawAsync(host.Client(), Turn("t-1", "r-1", Rooftop));

        var warranty = InvocationOf(frames, "warranty-specialist");
        var error = frames.Single(f => f.Type() == "SUBAGENT_ERROR");
        Assert.Equal(warranty, error.Owner());
        Assert.Equal("timeout", error.Str("code"));
        Assert.Equal("The warranty specialist didn't answer within 1 second.", error.Str("message"));

        // The lookup it was waiting on gets a result saying so, before the invocation closes.
        var call = frames.Single(f => f.Type() == "TOOL_CALL_START" && f.Owner() == warranty);
        var result = frames.Single(f => f.Type() == "TOOL_CALL_RESULT" && f.Str("toolCallId") == call.Str("toolCallId"));
        Assert.Equal(warranty, result.Owner());
        Assert.StartsWith("The call was abandoned.", JsonDocument.Parse(result.Str("content")!).RootElement.Str("error"), StringComparison.Ordinal);

        // SUBAGENT_ERROR ends the invocation, not the run: the others finish and the coordinator answers.
        Assert.Equal(2, frames.Count(f => f.Type() == "SUBAGENT_FINISHED"));
        Assert.DoesNotContain(frames, f => f.Type() == "RUN_ERROR");
        Assert.Equal("success", frames[^1].GetProperty("outcome").Str("type"));
        Assert.Contains("**Warranty specialist:** no answer.", TextOf(frames, owner: null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_specialist_whose_model_fails_reports_a_sanitized_SUBAGENT_ERROR()
    {
        await using var host = new AgentHost();
        host.Model.FailWhenOffered = "get_schedule"; // only the scheduling specialist's model calls offer it

        var frames = await RawAsync(host.Client(), Turn("t-1", "r-1", Capacitor));

        var error = frames.Single(f => f.Type() == "SUBAGENT_ERROR");
        Assert.Equal(InvocationOf(frames, "scheduling-specialist"), error.Owner());
        Assert.Equal("specialist_failed", error.Str("code"));
        Assert.DoesNotContain("contoso.internal", error.GetRawText(), StringComparison.Ordinal); // the exception stays in the log
        Assert.Equal("success", frames[^1].GetProperty("outcome").Str("type"));
    }

    [Fact]
    public async Task A_specialist_that_needs_an_approval_suspends_and_the_interrupt_names_it()
    {
        await using var host = new AgentHost();
        var frames = await RawAsync(host.Client(), Turn("t-1", "r-1", Compressor));

        var parts = InvocationOf(frames, "parts-specialist");
        var suspended = frames.Single(f => f.Type() == "SUBAGENT_FINISHED" && f.Owner() == parts).GetProperty("outcome");
        Assert.Equal("suspended", suspended.Str("type"));
        var interruptId = Assert.Single(suspended.GetProperty("interruptIds").EnumerateArray()).GetString();

        // The run ends asking, and the interrupt says who asked and about which of its calls.
        var interrupt = Interrupt(frames);
        Assert.Equal(interruptId, interrupt.Str("id"));
        Assert.Equal(parts, interrupt.Owner());
        Assert.Equal("tool_call", interrupt.Str("reason"));
        var call = frames.Single(f => f.Type() == "TOOL_CALL_START" && f.Str("toolCallName") == "place_part_order");
        Assert.Equal(parts, call.Owner());
        Assert.Equal(call.Str("toolCallId"), interrupt.Str("toolCallId"));
        Assert.DoesNotContain(frames, f => f.Type() == "TOOL_CALL_RESULT" && f.Str("toolCallId") == call.Str("toolCallId"));

        // The other two finished; nothing was ordered.
        Assert.Equal(2, frames.Count(f => f.Type() == "SUBAGENT_FINISHED" && !f.TryGetProperty("outcome", out _)));
        Assert.Empty(host.Store.ListPartOrders());
        Assert.Contains("Waiting for your approval: Order 1 x CMP-CA36", TextOf(frames, owner: null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approving_continues_the_same_invocation_and_it_places_the_order()
    {
        await using var host = new AgentHost();
        var http = host.Client();
        var first = await RawAsync(http, Turn("t-1", "r-1", Compressor));
        var parts = InvocationOf(first, "parts-specialist");
        var interrupt = Interrupt(first);

        var second = await RawAsync(http, Resume("t-1", "r-2", Compressor, SdkApproval(interrupt.Str("id")!, approved: true, interrupt.Str("toolCallId")!)));

        // The same ID comes back: a continuation, not a new invocation, and only that one.
        var restarted = Assert.Single(second, f => f.Type() == "SUBAGENT_STARTED");
        Assert.Equal(parts, restarted.Owner());
        Assert.Equal(["resuming", "answering"], second.Where(f => f.Type() == "STEP_STARTED").Select(f => f.Str("stepName")));

        // The approved call isn't announced again; its result arrives attributed to the parts specialist.
        Assert.DoesNotContain(second, f => f.Type() == "TOOL_CALL_START");
        var result = second.Single(f => f.Type() == "TOOL_CALL_RESULT");
        Assert.Equal((interrupt.Str("toolCallId"), parts), (result.Str("toolCallId"), result.Owner()));

        var order = Assert.Single(host.Store.ListPartOrders());
        Assert.Equal(("CMP-CA36", 1), (order.Sku, order.Quantity));
        Assert.Equal($"Ordered 1 x CMP-CA36 for WO-1001 as {order.Id}, $1,795.00.", TextOf(second, parts));
        Assert.False(second.Single(f => f.Type() == "SUBAGENT_FINISHED").TryGetProperty("outcome", out _));
        Assert.Equal("success", second[^1].GetProperty("outcome").Str("type"));
    }

    [Fact]
    public async Task Declining_continues_the_invocation_without_ordering()
    {
        await using var host = new AgentHost();
        var http = host.Client();
        var first = await RawAsync(http, Turn("t-1", "r-1", Compressor));
        var interrupt = Interrupt(first);

        var second = await RawAsync(http, Resume("t-1", "r-2", Compressor, SdkApproval(interrupt.Str("id")!, approved: false, interrupt.Str("toolCallId")!)));

        Assert.Empty(host.Store.ListPartOrders());
        Assert.StartsWith("Nothing was ordered for WO-1001.", TextOf(second, InvocationOf(second, "parts-specialist")), StringComparison.Ordinal);
        Assert.Equal("success", second[^1].GetProperty("outcome").Str("type"));
    }

    [Fact]
    public async Task A_replayed_or_forged_resume_is_refused_before_anything_streams()
    {
        await using var host = new AgentHost();
        var http = host.Client();
        var first = await RawAsync(http, Turn("t-1", "r-1", Compressor));
        var interrupt = Interrupt(first);
        var approve = Resume("t-1", "r-2", Compressor, SdkApproval(interrupt.Str("id")!, approved: true, interrupt.Str("toolCallId")!));
        await RawAsync(http, approve);

        using var replayed = await PostAsync(http, approve);
        using var forged = await PostAsync(http, Resume("t-2", "r-9", Compressor, SdkApproval("apr_00000000000000000000000000000000", approved: true, "call-0001-1")));

        Assert.Equal(HttpStatusCode.Conflict, replayed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, forged.StatusCode);
        Assert.Single(host.Store.ListPartOrders());
    }

    [Fact]
    public async Task No_protocol_field_is_ever_written_as_null()
    {
        await using var host = new AgentHost();
        var http = host.Client();
        List<JsonElement> frames = [.. await RawAsync(http, Turn("t-1", "r-1", Capacitor))];
        frames.AddRange(await RawAsync(http, Turn("t-2", "r-1", Rooftop)));
        var asking = await RawAsync(http, Turn("t-3", "r-1", Compressor));
        frames.AddRange(asking);
        var interrupt = Interrupt(asking);
        frames.AddRange(await RawAsync(http, Resume("t-3", "r-2", Compressor, SdkApproval(interrupt.Str("id")!, true, interrupt.Str("toolCallId")!))));

        Assert.Contains(frames, f => f.Type() == "SUBAGENT_ERROR");
        Assert.Empty(frames.SelectMany(f => AGUIWire.NullProperties(f)));
    }

    [Fact]
    public async Task The_coordinators_model_never_rereads_the_specialists_messages()
    {
        await using var host = new AgentHost();
        var http = host.Client();
        var first = await RawAsync(http, Turn("t-1", "r-1", Capacitor));
        var parts = InvocationOf(first, "parts-specialist");

        // The next turn's history, as a client sends it: the specialists' messages attributed to them.
        var input = Turn("t-1", "r-2", "Thanks. Now check the warranty on WO-1001 again.");
        input.Messages =
        [
            new AGUI.Abstractions.AGUIUserMessage { Id = "user-1", Content = Capacitor },
            new AGUI.Abstractions.AGUIAssistantMessage { Id = "msg-parts", Content = TextOf(first, parts), SubagentRunId = parts },
            new AGUI.Abstractions.AGUIAssistantMessage { Id = "msg-answer", Content = TextOf(first, owner: null) },
            new AGUI.Abstractions.AGUIUserMessage { Id = "user-2", Content = "Thanks. Now check the warranty on WO-1001 again." },
        ];
        await RawAsync(http, input);

        // Every model call that offered the delegation tools was the coordinator's.
        var coordinatorCalls = host.Model.Calls.Where(c => c.Tools.Contains("ask_parts_specialist")).ToList();
        Assert.NotEmpty(coordinatorCalls);
        Assert.All(coordinatorCalls, call => Assert.DoesNotContain(call.Messages, m => m.MessageId == "msg-parts"));
        Assert.Contains(coordinatorCalls[^1].Messages, m => m.MessageId == "msg-answer");
    }

    [Fact]
    public async Task A_delegation_call_the_coordinator_cant_use_never_reaches_the_wire()
    {
        await using var host = new AgentHost();

        // A real model can call a delegation tool without a task. Nothing is delegated, so what it said while
        // planning becomes the answer, without the call.
        host.Model.AnswerWhen = tools => tools.Contains("ask_parts_specialist")
            ? [new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("I couldn't tell which part you meant. "), new FunctionCallContent("call-plan-1", "ask_parts_specialist", new Dictionary<string, object?> { ["task"] = "" })]) { MessageId = "msg-plan" }]
            : null;

        var frames = await RawAsync(host.Client(), Turn("t-1", "r-1", Capacitor));

        Assert.DoesNotContain(frames, f => f.Type().StartsWith("TOOL_CALL_", StringComparison.Ordinal));
        Assert.DoesNotContain(frames, f => f.Type() == "SUBAGENT_STARTED");
        Assert.Equal("I couldn't tell which part you meant. ", TextOf(frames, owner: null));
        Assert.Equal("success", frames[^1].GetProperty("outcome").Str("type"));
    }

    private static int IndexOf(IReadOnlyList<JsonElement> frames, Func<JsonElement, bool> predicate) => Indexes(frames, predicate).First();

    private static List<int> Indexes(IReadOnlyList<JsonElement> frames, Func<JsonElement, bool> predicate) =>
        [.. frames.Select((f, i) => (f, i)).Where(x => predicate(x.f)).Select(x => x.i)];
}
