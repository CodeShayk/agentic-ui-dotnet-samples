using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.AI;
using Ch14.Beacon.Agent.Approvals;
using Ch14.Tests.Infrastructure;

namespace Ch14.Tests;

/// <summary>The approval flow against the agent in memory: ask, then approve, deny or edit.</summary>
public sealed class ApprovalFlowTests
{
    [Fact]
    public async Task A_compressor_order_ends_the_run_with_an_interrupt_the_server_issued()
    {
        await using var host = new AgentHost();
        var http = host.Client();

        var (interrupt, callId) = await Runs.AskForCompressorAsync(http);

        // The server's own ID, not one derived from the call ID (FunctionInvokingChatClient's would be "ficc_" + callId).
        Assert.Matches("^apr_[0-9a-f]{32}$", interrupt.Id);
        Assert.Equal(InterruptReasons.ToolCall, interrupt.Reason);
        Assert.Equal("call-0001-1", callId);
        Assert.Contains("$1,795.00", interrupt.Message, StringComparison.Ordinal);
        Assert.Equal("2026-11-16T14:10:00Z", interrupt.ExpiresAt);
        Assert.Equal(1795.00m, interrupt.Metadata!.Value.GetProperty("amount").GetDecimal());

        // The schema says what an edit may change: quantity, and nothing pinned with "const".
        var edited = interrupt.ResponseSchema!.Value.GetProperty("properties").GetProperty("editedArgs").GetProperty("properties");
        Assert.Equal("CMP-CA36", edited.GetProperty("sku").GetProperty("const").GetString());
        Assert.Equal("integer", edited.GetProperty("quantity").GetProperty("type").GetString());

        // Nothing ordered yet; the approval is open and the audit has the request.
        Assert.Empty(host.Store.ListPartOrders());
        Assert.Equal(ApprovalStatus.Open, host.Approvals.Find("thread-1", interrupt.Id)!.Status);
        var requested = Assert.Single(host.Audit);
        Assert.Equal((ApprovalEvent.Requested, "desk-test", 1795.00m), (requested.Event, requested.ClientId, requested.Amount));
    }

    [Fact]
    public async Task A_capacitor_order_under_the_threshold_runs_without_asking()
    {
        await using var host = new AgentHost();

        var events = await Runs.RunAsync(host.Client(), Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Order a spare run capacitor for WO-1001.")));

        Assert.IsType<RunFinishedSuccessOutcome>(Runs.Finished(events).Outcome);
        var order = Assert.Single(host.Store.ListPartOrders());
        Assert.Equal(("CAP-45-5", 38.50m), (order.Sku, order.Total));
        Assert.Contains("Order PO-9001 is placed", Runs.Text(events), StringComparison.Ordinal);

        var executed = Assert.Single(host.Audit);
        Assert.Equal((ApprovalEvent.Executed, "within the limit", "PO-9001"), (executed.Event, executed.Reason, executed.Result));
        Assert.Empty(host.Approvals.All);
    }

    [Fact]
    public async Task Approving_resumes_the_run_and_places_the_order_once()
    {
        await using var host = new AgentHost();
        var http = host.Client();
        var (interrupt, callId) = await Runs.AskForCompressorAsync(http);

        var events = await Runs.RunAsync(http, Runs.Resume("thread-1", "run-2",
            Runs.SdkApproval(interrupt.Id, approved: true, callId, BeaconTools.PlacePartOrderName, Runs.CompressorOrder)));

        // The resumed run restates the call (same ID, new parent message), then reports its result.
        var restated = Assert.Single(events.OfType<ToolCallStartEvent>());
        Assert.Equal(callId, restated.ToolCallId);
        Assert.Contains(events, e => e is ToolCallResultEvent r && r.ToolCallId == callId && r.Content.ToString().Contains("PO-9001", StringComparison.Ordinal));
        Assert.IsType<RunFinishedSuccessOutcome>(Runs.Finished(events).Outcome);

        var order = Assert.Single(host.Store.ListPartOrders());
        Assert.Equal(("CMP-CA36", 1, 1795.00m), (order.Sku, order.Quantity, order.Total));
        Assert.Equal(ApprovalStatus.Executed, host.Approvals.Find("thread-1", interrupt.Id)!.Status);
    }

    [Fact]
    public async Task Denying_places_nothing_and_tells_the_model_why()
    {
        await using var host = new AgentHost();
        var http = host.Client();
        var (interrupt, callId) = await Runs.AskForCompressorAsync(http);

        var events = await Runs.RunAsync(http, Runs.Resume("thread-1", "run-2", Runs.Answer(interrupt.Id, new JsonObject
        {
            ["approved"] = false,
            ["reason"] = "Hold off until Priya measures the compressor amperage.",
        })));

        Assert.Empty(host.Store.ListPartOrders());
        var result = events.OfType<ToolCallResultEvent>().Single(r => r.ToolCallId == callId);
        Assert.Equal("Tool call invocation rejected. The dispatcher declined it: Hold off until Priya measures the compressor amperage.", result.Content.ToString());
        Assert.StartsWith("Understood. Nothing was ordered.", Runs.Text(events), StringComparison.Ordinal);
        Assert.Equal(ApprovalStatus.Denied, host.Approvals.Find("thread-1", interrupt.Id)!.Status);
    }

    [Fact]
    public async Task An_edited_charge_is_repriced_and_runs_as_edited()
    {
        await using var host = new AgentHost();
        var http = host.Client();
        var asked = await Runs.RunAsync(http, Runs.Turn("thread-1", "run-1",
            Runs.User("user-1", "Charge Harbor View Dental $2,145.00 for the WO-1001 compressor replacement.")));
        var interrupt = Runs.SingleInterrupt(asked);
        Assert.Equal(2145.00m, interrupt.Metadata!.Value.GetProperty("amount").GetDecimal());

        // Harbor View's plan covers labor: Dana approves the parts only.
        var events = await Runs.RunAsync(http, Runs.Resume("thread-1", "run-2", Runs.Answer(interrupt.Id, new JsonObject
        {
            ["approved"] = true,
            ["reason"] = "Labor is covered by Harbor View's Priority plan.",
            ["editedArgs"] = new JsonObject
            {
                ["workOrderId"] = "WO-1001",
                ["amount"] = 1795.00m,
                ["description"] = "Compressor replacement, parts only",
            },
        })));

        var restatedArguments = events.OfType<ToolCallArgsEvent>().Single().Delta;
        Assert.Contains("1795", restatedArguments, StringComparison.Ordinal);
        Assert.Contains(events, e => e is ToolCallResultEvent r && r.Content.ToString().Contains("CHG-7001", StringComparison.Ordinal));

        var approval = host.Approvals.Find("thread-1", interrupt.Id)!;
        Assert.Equal((ApprovalStatus.Executed, 1795.00m), (approval.Status, approval.Decision!.EditedAmount));
        Assert.Contains(host.Audit, e => e.Event == ApprovalEvent.Edited && e.Amount == 1795.00m
                                         && e.Reason == "Labor is covered by Harbor View's Priority plan.");
    }

    [Theory]
    [InlineData("sku", "CAP-45-5", "sku cannot be changed when approving; only quantity can.")]
    [InlineData("quantity", 11, "The quantity must be a whole number from 1 to 10.")]
    [InlineData("quantity", "two", "The quantity must be a whole number from 1 to 10.")]
    public async Task An_edit_the_policy_rejects_is_refused_and_the_approval_stays_open(string argument, object value, string detail)
    {
        await using var host = new AgentHost();
        var http = host.Client();
        var (interrupt, _) = await Runs.AskForCompressorAsync(http);

        var edited = Runs.CompressorOrder;
        edited[argument] = JsonValue.Create(JsonSerializer.SerializeToElement(value));
        using var response = await Runs.PostAsync(http, Runs.Resume("thread-1", "run-2",
            Runs.Answer(interrupt.Id, new JsonObject { ["approved"] = true, ["reason"] = "Changed", ["editedArgs"] = edited })));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await Runs.ProblemAsync(response);
        Assert.Equal(("Invalid edit", detail), (problem.GetProperty("title").GetString(), problem.GetProperty("detail").GetString()));
        Assert.Equal(ApprovalStatus.Open, host.Approvals.Find("thread-1", interrupt.Id)!.Status);
        Assert.Empty(host.Store.ListPartOrders());
    }
}
