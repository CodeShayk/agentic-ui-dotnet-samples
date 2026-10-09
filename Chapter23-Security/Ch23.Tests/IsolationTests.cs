using AGUI.Abstractions;
using Beacon.AI;
using Ch23.Beacon.Security.Approvals;
using Ch23.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Ch23.Tests;

/// <summary>A thread ID names a conversation only within one user's conversations, and tools see only the caller's work.</summary>
public sealed class IsolationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_same_thread_ID_from_two_users_is_two_conversations()
    {
        await using var host = Hosts.Agent();

        await Runs.RunAsync(host.CreateClient(TestTokens.Dana),
            Runs.Turn("shared-thread", "run-1", Runs.User("user-1", "What is the status of WO-1001?")), Ct);

        // Priya knows (or guesses) the thread ID and asks what was discussed.
        await Runs.RunAsync(host.CreateClient(TestTokens.Priya),
            Runs.Turn("shared-thread", "run-2", Runs.User("user-2", "Hello, what did we talk about?")), Ct);

        var priyasRun = host.Recorder.Calls[^1].Messages;
        var only = Assert.Single(priyasRun);
        Assert.Equal("Hello, what did we talk about?", only.Text);
    }

    [Fact]
    public async Task A_technician_cannot_read_another_technicians_work_order()
    {
        await using var host = Hosts.Agent();

        var events = await Runs.RunAsync(host.CreateClient(TestTokens.Priya),
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "What is the status of WO-1005?")), Ct);

        // WO-1005 is Elena's. To Priya it doesn't exist, which also means she can't probe for it.
        var result = Assert.Single(events.OfType<ToolCallResultEvent>());
        Assert.Contains("Work order WO-1005 was not found.", result.Content.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_technician_sees_only_her_own_schedule_whatever_the_model_asks_for()
    {
        await using var host = Hosts.Agent();
        var priya = host.CreateClient(TestTokens.Priya);

        var hers = await Runs.RunAsync(priya, Runs.Turn("thread-1", "run-1", Runs.User("user-1", "What's on my schedule today?")), Ct);
        var schedule = Assert.Single(hers.OfType<ToolCallResultEvent>()).Content.ToString();
        Assert.Contains("TECH-1", schedule, StringComparison.Ordinal);
        Assert.DoesNotContain("TECH-3", schedule, StringComparison.Ordinal);

        var elenas = await Runs.RunAsync(priya, Runs.Turn("thread-2", "run-2", Runs.User("user-2", "Show the schedule for TECH-3")), Ct);
        Assert.Contains("only their own schedule", Assert.Single(elenas.OfType<ToolCallResultEvent>()).Content.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Technicians_are_never_offered_charge_customer()
    {
        await using var host = Hosts.Agent();

        await Runs.RunAsync(host.CreateClient(TestTokens.Priya),
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Charge WO-1001 $120")), Ct);

        var offered = host.Recorder.Calls[0].Options!.Tools!.Select(t => t.Name).ToList();
        Assert.Contains(BeaconTools.PlacePartOrderName, offered);
        Assert.DoesNotContain(BeaconTools.ChargeCustomerName, offered);
    }

    [Fact]
    public async Task Only_a_dispatcher_can_approve_so_a_technicians_large_order_is_refused_not_proposed()
    {
        await using var host = Hosts.Agent();

        var events = await Runs.RunAsync(host.CreateClient(TestTokens.Priya),
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Order CMP-CA36 for WO-1001")), Ct);

        // Nobody on Priya's thread could approve $1,795.00, so she isn't asked; the check at the tool says who can.
        Assert.IsType<RunFinishedSuccessOutcome>(Runs.Finished(events).Outcome);
        Assert.Contains("needs a dispatcher", Assert.Single(events.OfType<ToolCallResultEvent>()).Content.ToString(), StringComparison.Ordinal);
        Assert.Empty(ApprovalEnforcementTests.Store(host).ListPartOrders());
        Assert.Empty(host.Services.GetRequiredService<IApprovalStore>().All);
    }

    [Fact]
    public async Task A_technicians_order_within_the_limit_runs_and_is_audited_under_her_name()
    {
        await using var host = Hosts.Agent();

        await Runs.RunAsync(host.CreateClient(TestTokens.Priya),
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Order CAP-45-5 for WO-1001")), Ct);

        Assert.Equal("CAP-45-5", Assert.Single(ApprovalEnforcementTests.Store(host).ListPartOrders()).Sku);
        var executed = Assert.Single(ApprovalEnforcementTests.Audit(host).Entries);
        Assert.Equal((ApprovalEvent.Executed, "priya", "within the limit"), (executed.Event, executed.UserId, executed.Reason));
    }
}
