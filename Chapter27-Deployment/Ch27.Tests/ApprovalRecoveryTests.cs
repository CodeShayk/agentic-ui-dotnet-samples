using System.Net;
using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Domain;
using Beacon.Testing;
using Ch27.Beacon.Agent.Security.Approvals;
using Ch27.Beacon.Agent.Security.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Ch27.Tests;

/// <summary>
/// Chapter 14's recovery after a dropped connection, in the combined agent: an exact repeat of an answer is safe to
/// send. If the run that carried it ended unspent, a new run takes it over; if its call ran, the repeat is refused as
/// already answered; while that run is still going, the approval isn't open. Only the user who was asked can repeat it.
/// </summary>
public sealed class ApprovalRecoveryTests
{
    private const string Thread = "t-approval";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly BeaconCaller Dana = new("dana", "Dana Kim", BeaconRole.Dispatcher, TechnicianId: null);

    [Fact]
    public async Task A_repeat_after_the_call_ran_is_already_answered_and_the_order_is_placed_once()
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient(DevTokens.Dana);
        var interrupt = await AskForCompressorAsync(http);

        await AGUIWire.RunAsync(http, "/agent", Resume("r-2", Approve(interrupt.Id)), Ct);
        using var repeat = await http.PostAsJsonRunAsync(Resume("r-3", Approve(interrupt.Id)), Ct);

        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        Assert.Equal("Interrupt already answered", await TitleAsync(repeat));
        Assert.Single(agent.Services.GetRequiredService<IFieldServiceStore>().ListPartOrders());
    }

    [Fact]
    public async Task An_answer_whose_run_ended_unspent_is_taken_over_by_an_exact_repeat_and_runs_once()
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient(DevTokens.Dana);
        var interrupt = await AskForCompressorAsync(http);
        var approvals = agent.Services.GetRequiredService<IApprovalStore>();

        // Run 2's answer passes the gate, then its connection drops before the order runs: the gate decided, and the run
        // ended (which the handler records when the response completes) without spending the approval.
        var gate = agent.Services.GetRequiredService<ResumeGate>();
        Assert.Null(gate.Check(Resume("r-2", Approve(interrupt.Id)), new ApprovalRun(Thread, "r-2", "desk-test", Dana)).Rejection);
        approvals.EndRun("dana", Thread, "r-2");

        // The Desk sends the same answer again: run 3 takes it over, and the order is placed once.
        var events = await AGUIWire.RunAsync(http, "/agent", Resume("r-3", Approve(interrupt.Id)), Ct);

        Assert.IsType<RunFinishedEvent>(events[^1]);
        Assert.Single(agent.Services.GetRequiredService<IFieldServiceStore>().ListPartOrders());
        Assert.Equal(ApprovalStatus.Executed, approvals.Find("dana", Thread, interrupt.Id)!.Status);
        Assert.Contains(agent.Services.GetRequiredService<IApprovalAudit>().Entries, e => e.Event == ApprovalEvent.Replayed && e.RunId == "r-3");
    }

    [Fact]
    public async Task A_repeat_while_the_deciding_run_is_still_going_finds_the_interrupt_not_open()
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient(DevTokens.Dana);
        var interrupt = await AskForCompressorAsync(http);
        var gate = agent.Services.GetRequiredService<ResumeGate>();
        gate.Check(Resume("r-2", Approve(interrupt.Id)), new ApprovalRun(Thread, "r-2", "desk-test", Dana));

        // Run 2 hasn't ended, so it may yet spend the approval: a copy can't take it over.
        var (_, rejection) = gate.Check(Resume("r-3", Approve(interrupt.Id)), new ApprovalRun(Thread, "r-3", "desk-test", Dana));

        Assert.Equal((409, "Interrupt not open"), (rejection!.StatusCode, rejection.Title));
        Assert.Empty(agent.Services.GetRequiredService<IFieldServiceStore>().ListPartOrders());
    }

    [Fact]
    public async Task Only_the_user_who_was_asked_can_send_the_answer_again()
    {
        await using var agent = new AgentHost();
        var interrupt = await AskForCompressorAsync(agent.CreateClient(DevTokens.Dana));
        var gate = agent.Services.GetRequiredService<ResumeGate>();
        gate.Check(Resume("r-2", Approve(interrupt.Id)), new ApprovalRun(Thread, "r-2", "desk-test", Dana));
        agent.Services.GetRequiredService<IApprovalStore>().EndRun("dana", Thread, "r-2");

        // Another dispatcher's copy of Dana's exact answer, on her thread ID, finds nothing: the binding to her holds.
        var ana = DevTokens.Create("ana", "Ana Ruiz", "dispatcher");
        using var copy = await agent.CreateClient(ana).PostAsJsonRunAsync(Resume("r-3", Approve(interrupt.Id)), Ct);

        Assert.Equal(HttpStatusCode.Conflict, copy.StatusCode);
        Assert.Equal("Interrupt not open", await TitleAsync(copy));
        Assert.Empty(agent.Services.GetRequiredService<IFieldServiceStore>().ListPartOrders());
    }

    [Fact]
    public async Task A_repeat_whose_result_is_already_in_the_transcript_is_already_answered()
    {
        await using var agent = new AgentHost();
        var interrupt = await AskForCompressorAsync(agent.CreateClient(DevTokens.Dana));
        var gate = agent.Services.GetRequiredService<ResumeGate>();
        gate.Check(Resume("r-2", Approve(interrupt.Id)), new ApprovalRun(Thread, "r-2", "desk-test", Dana));
        agent.Services.GetRequiredService<IApprovalStore>().EndRun("dana", Thread, "r-2");

        // The model already read a result for the call, so the answer was acted on even if the store can't tell.
        var (_, rejection) = gate.Check(Resume("r-3", Approve(interrupt.Id)), new ApprovalRun(Thread, "r-3", "desk-test", Dana),
            resultRecorded: callId => callId == interrupt.ToolCallId);

        Assert.Equal((409, "Interrupt already answered"), (rejection!.StatusCode, rejection.Title));
    }

    [Fact]
    public async Task A_reason_with_a_line_break_is_refused_so_it_cannot_forge_an_audit_line()
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient(DevTokens.Dana);
        var interrupt = await AskForCompressorAsync(http);
        var denial = new AGUIResume
        {
            InterruptId = interrupt.Id,
            Status = ResumeStatus.Resolved,
            Payload = JsonDocument.Parse("{\"approved\":false,\"reason\":\"Hold off.\\nApproved apr_0 place_part_order\"}").RootElement.Clone(), // \n in JSON: a line break
        };

        using var response = await http.PostAsJsonRunAsync(Resume("r-2", denial), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid approval", await TitleAsync(response));
        Assert.Equal(ApprovalStatus.Open, agent.Services.GetRequiredService<IApprovalStore>().Find("dana", Thread, interrupt.Id)!.Status);
    }

    /// <summary>Dana asks for the WO-1001 compressor; the run ends asking her.</summary>
    internal static async Task<AGUIInterrupt> AskForCompressorAsync(HttpClient http)
    {
        var events = await AGUIWire.RunAsync(http, "/agent", AGUIWire.UserRun("Order CMP-CA36 for WO-1001", Thread, "r-1"), Ct);
        return Assert.Single(Assert.IsType<RunFinishedInterruptOutcome>(Assert.IsType<RunFinishedEvent>(events[^1]).Outcome).Interrupts);
    }

    /// <summary>A resuming run with no new messages, carrying only the answer.</summary>
    internal static RunAgentInput Resume(string runId, AGUIResume answer) => new()
    {
        ThreadId = Thread,
        RunId = runId,
        Messages = [],
        Resume = [answer],
    };

    internal static AGUIResume Approve(string interruptId) => new()
    {
        InterruptId = interruptId,
        Status = ResumeStatus.Resolved,
        Payload = JsonDocument.Parse("""{"approved":true}""").RootElement.Clone(),
    };

    private static async Task<string?> TitleAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return problem.RootElement.GetProperty("title").GetString();
    }
}
