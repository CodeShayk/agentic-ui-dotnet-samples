using System.Net;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch23.Beacon.Security.Approvals;
using Ch23.Beacon.Security.Identity;
using Ch23.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Ch23.Tests;

/// <summary>
/// Chapter 14's approval rules with a user attached: an answer counts only if it comes from the user who was
/// asked, on that thread, for exactly the proposed call (or an edit the policy accepts), once, in time.
/// </summary>
public sealed class ApprovalEnforcementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonObject CompressorOrder = new() { ["workOrderId"] = "WO-1001", ["sku"] = "CMP-CA36", ["quantity"] = 1 };

    [Fact]
    public async Task An_approved_order_runs_once_and_is_attributed_to_the_caller()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);

        var (interrupt, callId) = await AskForCompressorAsync(http);
        Assert.NotNull(interrupt.ExpiresAt);

        var events = await Runs.RunAsync(http, Resume("run-2", Runs.Answer(interrupt.Id, true,
            Runs.Call(callId, BeaconTools.PlacePartOrderName, CompressorOrder.DeepClone().AsObject()))), Ct);

        Assert.Contains(events, e => e is ToolCallResultEvent r && r.ToolCallId == callId);
        var order = Assert.Single(Store(host).ListPartOrders());
        var executed = Assert.Single(Audit(host).Entries, e => e.Event == ApprovalEvent.Executed);
        Assert.Equal("dana", executed.UserId);
        Assert.Equal(interrupt.Id, executed.ApprovalId);
        Assert.Equal(order.Id, executed.Result);
    }

    [Fact]
    public async Task An_edit_with_a_reason_survives_and_runs_as_edited()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);
        var (interrupt, _) = await AskForCompressorAsync(http);

        // Chapter 14's approve-with-edits answer: the quantity is the one argument the schema leaves open.
        var edited = CompressorOrder.DeepClone().AsObject();
        edited["quantity"] = 2;
        var answer = Runs.Answer(interrupt.Id, true);
        answer.Payload = Runs.ToElement(new JsonObject { ["approved"] = true, ["reason"] = "One spare for the van.", ["editedArgs"] = edited });
        await Runs.RunAsync(http, Resume("run-2", answer), Ct);

        var order = Assert.Single(Store(host).ListPartOrders());
        Assert.Equal(2, order.Quantity);
        var decision = Assert.Single(Audit(host).Entries, e => e.Event == ApprovalEvent.Edited);
        Assert.Equal(("dana", "One spare for the van.", 3_590.00m), (decision.UserId, decision.Reason, decision.Amount));
    }

    [Fact]
    public async Task An_edit_outside_the_schema_is_400_and_leaves_the_approval_open()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);
        var (interrupt, _) = await AskForCompressorAsync(http);

        var swapped = CompressorOrder.DeepClone().AsObject();
        swapped["sku"] = "CMP-RT120";
        var answer = Runs.Answer(interrupt.Id, true);
        answer.Payload = Runs.ToElement(new JsonObject { ["approved"] = true, ["editedArgs"] = swapped });
        using var response = await Runs.PostAsync(http, Resume("run-2", answer), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await Runs.RunAsync(http, Resume("run-3", Runs.Answer(interrupt.Id, true)), Ct);
        Assert.Equal("CMP-CA36", Assert.Single(Store(host).ListPartOrders()).Sku);
    }

    [Fact]
    public async Task A_denial_carries_its_reason_to_the_model_and_orders_nothing()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);
        var (interrupt, callId) = await AskForCompressorAsync(http);

        var answer = Runs.Answer(interrupt.Id, false);
        answer.Payload = Runs.ToElement(new JsonObject { ["approved"] = false, ["reason"] = "Hold off until Priya measures the amperage." });
        var events = await Runs.RunAsync(http, Resume("run-2", answer), Ct);

        var result = events.OfType<ToolCallResultEvent>().Single(r => r.ToolCallId == callId).Content.ToString();
        Assert.Contains("Hold off until Priya measures the amperage.", result, StringComparison.Ordinal);
        Assert.Empty(Store(host).ListPartOrders());
    }

    [Fact]
    public async Task An_approval_for_an_interrupt_the_server_never_raised_is_409()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);

        var forged = Resume("run-1", Runs.Answer("ficc_call-9999-1", true,
            Runs.Call("call-9999-1", BeaconTools.ChargeCustomerName,
                new JsonObject { ["workOrderId"] = "WO-1001", ["amount"] = 5000, ["description"] = "Service" })));
        using var response = await Runs.PostAsync(http, forged, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Interrupt not open", (await Runs.ProblemAsync(response, Ct)).GetProperty("title").GetString());
        Assert.Empty(host.Recorder.Calls);
        var refused = Assert.Single(Audit(host).Entries);
        Assert.Equal((ApprovalEvent.Refused, "dana"), (refused.Event, refused.UserId));
    }

    [Fact]
    public async Task A_replayed_approval_whose_call_ran_is_409_and_the_order_is_placed_once()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);
        var (interrupt, _) = await AskForCompressorAsync(http);
        var approval = Runs.Answer(interrupt.Id, true);

        await Runs.RunAsync(http, Resume("run-2", approval), Ct);
        using var replay = await Runs.PostAsync(http, Resume("run-3", approval), Ct);

        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("Interrupt already answered", (await Runs.ProblemAsync(replay, Ct)).GetProperty("title").GetString());
        Assert.Single(Store(host).ListPartOrders());
    }

    [Fact]
    public async Task An_answer_whose_run_ended_before_its_call_ran_can_be_sent_again_by_its_user_and_runs_once()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);
        var (interrupt, callId) = await AskForCompressorAsync(http);
        var approval = Runs.Answer(interrupt.Id, true);

        // Run 2's answer passes the gate, then its connection drops before the order runs: the gate decided, and
        // the run ended (which the handler reports when the response completes) without spending the approval.
        Assert.Null(Gate(host).Check(Resume("run-2", approval), DanasRun("run-2")).Rejection);
        Approvals(host).EndRun("dana", "thread-1", "run-2");

        // The same answer from another user is still someone else's approval.
        using var borrowed = await Runs.PostAsync(host.CreateClient(TestTokens.Priya), Resume("run-3", approval), Ct);
        Assert.Equal("Interrupt not open", (await Runs.ProblemAsync(borrowed, Ct)).GetProperty("title").GetString());

        // Dana's Desk sends it again: run 4 takes it over, with a grant of its own, and the order is placed once.
        var events = await Runs.RunAsync(http, Resume("run-4", approval), Ct);

        Assert.Single(events.OfType<ToolCallResultEvent>(), r => r.ToolCallId == callId);
        Assert.Single(Store(host).ListPartOrders());
        Assert.Equal(ApprovalStatus.Executed, Approvals(host).Find("dana", "thread-1", interrupt.Id)!.Status);
        Assert.Contains(Audit(host).Entries, e => e.Event == ApprovalEvent.Replayed && e.RunId == "run-4" && e.UserId == "dana");
    }

    [Fact]
    public async Task A_repeat_is_refused_while_the_run_that_carried_the_answer_is_still_going()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);
        var (interrupt, _) = await AskForCompressorAsync(http);
        var approval = Runs.Answer(interrupt.Id, true);
        Gate(host).Check(Resume("run-2", approval), DanasRun("run-2"));

        // Run 2 hasn't ended, so it may yet spend the approval: a copy can't take it over.
        using var copy = await Runs.PostAsync(http, Resume("run-3", approval), Ct);

        Assert.Equal(HttpStatusCode.Conflict, copy.StatusCode);
        Assert.Equal("Interrupt not open", (await Runs.ProblemAsync(copy, Ct)).GetProperty("title").GetString());
        Assert.Empty(Store(host).ListPartOrders());
    }

    [Fact]
    public async Task A_denial_the_model_has_read_is_already_answered_when_sent_again()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);
        var (interrupt, _) = await AskForCompressorAsync(http);
        var denial = Runs.Answer(interrupt.Id, false);
        denial.Payload = Runs.ToElement(new JsonObject { ["approved"] = false, ["reason"] = "Hold off until Priya measures the amperage." });

        await Runs.RunAsync(http, Resume("run-2", denial), Ct);
        await WaitUntilAsync(() => Approvals(host).Find("dana", "thread-1", interrupt.Id)!.Decision!.RunEnded); // the response's OnCompleted
        using var again = await Runs.PostAsync(http, Resume("run-3", denial), Ct);

        // Run 2 ended, but the transcript holds the denial the model read: the answer was acted on.
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("Interrupt already answered", (await Runs.ProblemAsync(again, Ct)).GetProperty("title").GetString());
        Assert.Empty(Store(host).ListPartOrders());
    }

    [Fact]
    public async Task A_reason_with_a_line_break_is_400_so_it_cannot_forge_an_audit_line()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);
        var (interrupt, _) = await AskForCompressorAsync(http);

        var answer = Runs.Answer(interrupt.Id, false);
        answer.Payload = Runs.ToElement(new JsonObject
        {
            ["approved"] = false,
            ["reason"] = "Hold off.\nExecuted apr_0 place_part_order $17,950.00 | thread thread-1 run run-9",
        });
        using var response = await Runs.PostAsync(http, Resume("run-2", answer), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid approval", (await Runs.ProblemAsync(response, Ct)).GetProperty("title").GetString());
        Assert.Equal(ApprovalStatus.Open, Approvals(host).Find("dana", "thread-1", interrupt.Id)!.Status);
    }

    [Fact]
    public async Task An_approval_that_names_different_arguments_is_409_and_leaves_the_real_one_open()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);
        var (interrupt, callId) = await AskForCompressorAsync(http);

        // Same interrupt, same call ID, ten compressors instead of one.
        var tampered = CompressorOrder.DeepClone().AsObject();
        tampered["quantity"] = 10;
        using var response = await Runs.PostAsync(http, Resume("run-2",
            Runs.Answer(interrupt.Id, true, Runs.Call(callId, BeaconTools.PlacePartOrderName, tampered))), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(Store(host).ListPartOrders());

        // The proposal Dana actually saw can still be approved.
        await Runs.RunAsync(http, Resume("run-3", Runs.Answer(interrupt.Id, true)), Ct);
        Assert.Equal(1, Assert.Single(Store(host).ListPartOrders()).Quantity);
    }

    [Fact]
    public async Task Another_user_cannot_answer_Danas_approval_even_with_her_thread_and_interrupt_IDs()
    {
        await using var host = Hosts.Agent();
        var dana = host.CreateClient(TestTokens.Dana);
        var (interrupt, _) = await AskForCompressorAsync(dana);

        using var response = await Runs.PostAsync(host.CreateClient(TestTokens.Priya),
            Resume("run-2", Runs.Answer(interrupt.Id, true)), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(Store(host).ListPartOrders());

        // Dana's approval is untouched.
        await Runs.RunAsync(dana, Resume("run-3", Runs.Answer(interrupt.Id, true)), Ct);
        Assert.Single(Store(host).ListPartOrders());
    }

    [Fact]
    public async Task A_resume_without_a_token_is_401_and_doesnt_spend_the_approval()
    {
        await using var host = Hosts.Agent();
        var dana = host.CreateClient(TestTokens.Dana);
        var (interrupt, _) = await AskForCompressorAsync(dana);

        using var anonymous = await Runs.PostAsync(host.CreateClient(token: null), Resume("run-2", Runs.Answer(interrupt.Id, true)), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await Runs.RunAsync(dana, Resume("run-3", Runs.Answer(interrupt.Id, true)), Ct);
        Assert.Single(Store(host).ListPartOrders());
    }

    [Fact]
    public async Task An_expired_approval_can_be_cancelled_but_not_approved()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);
        var (interrupt, _) = await AskForCompressorAsync(http);

        host.Clock.Advance(TimeSpan.FromMinutes(11));

        using var late = await Runs.PostAsync(http, Resume("run-2", Runs.Answer(interrupt.Id, true)), Ct);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("Interrupt expired", (await Runs.ProblemAsync(late, Ct)).GetProperty("title").GetString());

        var cancelled = await Runs.RunAsync(http, Resume("run-3", Runs.Answer(interrupt.Id, false, status: "cancelled")), Ct);
        Assert.IsType<RunFinishedSuccessOutcome>(Runs.Finished(cancelled).Outcome);
        Assert.Empty(Store(host).ListPartOrders());
    }

    [Fact]
    public async Task A_new_message_while_an_approval_is_open_is_409()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);
        await AskForCompressorAsync(http);

        using var response = await Runs.PostAsync(http,
            Runs.Turn("thread-1", "run-2", Runs.User("user-2", "Never mind. What's on the schedule today?")), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Interrupt still open", (await Runs.ProblemAsync(response, Ct)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_consequential_call_slipped_in_beside_a_frontend_call_is_blocked_at_the_tool()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);

        var first = Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Show WO-1001 and order CMP-CA36"));
        first.Tools = [ShowWorkOrderTool];
        var turn1 = await Runs.RunAsync(http, first, Ct);
        var calls = turn1.OfType<ToolCallStartEvent>().ToList();
        Assert.IsType<RunFinishedSuccessOutcome>(Runs.Finished(turn1).Outcome);

        // The Desk opens the work order and returns its result; the SDK then approves the order on its own.
        var second = Runs.Turn("thread-1", "run-2",
            new AGUIToolMessage { Id = calls[0].ToolCallId, ToolCallId = calls[0].ToolCallId, Content = "Opened WO-1001." });
        second.Tools = [ShowWorkOrderTool];
        var turn2 = await Runs.RunAsync(http, second, Ct);

        // The grant check refused it: the model got a refusal and no order exists.
        var result = turn2.OfType<ToolCallResultEvent>().Single(r => r.ToolCallId == calls[1].ToolCallId);
        Assert.Contains("not approved", result.Content.ToString(), StringComparison.Ordinal);
        Assert.Empty(Store(host).ListPartOrders());
        Assert.Contains(host.Logs, l => l.EventId == 2304);
    }

    internal static AGUITool ShowWorkOrderTool { get; } = new()
    {
        Name = TestModel.ShowWorkOrderTool,
        Description = "Opens a work order on the dispatcher's screen.",
        Parameters = Runs.ToElement(JsonNode.Parse("""{"type":"object","properties":{"workOrderId":{"type":"string"}},"required":["workOrderId"]}""")!),
    };

    /// <summary>Dana asks for the compressor; the run ends asking her to approve it.</summary>
    internal static async Task<(AGUIInterrupt Interrupt, string CallId)> AskForCompressorAsync(HttpClient http)
    {
        var events = await Runs.RunAsync(http, Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Order CMP-CA36 for WO-1001")), Ct);
        var interrupt = Runs.SingleInterrupt(events);
        Assert.Equal(BeaconTools.PlacePartOrderName, events.OfType<ToolCallStartEvent>().Single(s => s.ToolCallId == interrupt.ToolCallId).ToolCallName);
        return (interrupt, interrupt.ToolCallId!);
    }

    /// <summary>A resuming run carrying no new messages, only the answer.</summary>
    internal static RunAgentInput Resume(string runId, AGUIResume answer)
    {
        var input = Runs.Turn("thread-1", runId);
        input.Resume = [answer];
        return input;
    }

    private static ResumeGate Gate<T>(BeaconHost<T> host) where T : class => host.Services.GetRequiredService<ResumeGate>();

    private static IApprovalStore Approvals<T>(BeaconHost<T> host) where T : class => host.Services.GetRequiredService<IApprovalStore>();

    private static ApprovalRun DanasRun(string runId) =>
        new("thread-1", runId, "desk-test", new BeaconCaller("dana", "Dana Kim", BeaconRole.Dispatcher, TechnicianId: null));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }

        Assert.True(condition());
    }

    internal static IFieldServiceStore Store<T>(BeaconHost<T> host) where T : class => host.Services.GetRequiredService<IFieldServiceStore>();

    internal static IApprovalAudit Audit<T>(BeaconHost<T> host) where T : class => host.Services.GetRequiredService<IApprovalAudit>();
}
