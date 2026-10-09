using System.Text.Json.Nodes;
using Beacon.AI;
using Ch14.Beacon.Agent.Approvals;
using Ch14.Tests.Infrastructure;

namespace Ch14.Tests;

/// <summary>Who approved what, when and from which client.</summary>
public sealed class AuditTests
{
    [Fact]
    public async Task The_audit_records_who_decided_what_when_and_from_which_client()
    {
        await using var host = new AgentHost();

        // One Desk tab asks; the same thread is answered from another (Dana moved to her other screen).
        var (interrupt, callId) = await Runs.AskForCompressorAsync(host.Client("desk-1a2b3c4d"));
        host.Clock.Advance(TimeSpan.FromMinutes(3));
        await Runs.RunAsync(host.Client("desk-9f8e7d6c"), Runs.Resume("thread-1", "run-2",
            Runs.SdkApproval(interrupt.Id, approved: true, callId, BeaconTools.PlacePartOrderName, Runs.CompressorOrder)));

        Assert.Collection(host.Audit,
            requested =>
            {
                Assert.Equal((ApprovalEvent.Requested, "desk-1a2b3c4d", "run-1"), (requested.Event, requested.ClientId, requested.RunId));
                Assert.Equal(new DateTimeOffset(2026, 11, 16, 14, 0, 0, TimeSpan.Zero), requested.At);
                Assert.True(JsonNode.DeepEquals(Runs.CompressorOrder, JsonNode.Parse(requested.Arguments!)));
            },
            approved =>
            {
                Assert.Equal((ApprovalEvent.Approved, "desk-9f8e7d6c", "run-2"), (approved.Event, approved.ClientId, approved.RunId));
                Assert.Equal(new DateTimeOffset(2026, 11, 16, 14, 3, 0, TimeSpan.Zero), approved.At);
                Assert.Equal((interrupt.Id, callId, 1795.00m), (approved.ApprovalId, approved.ToolCallId, approved.Amount));
            },
            executed => Assert.Equal((ApprovalEvent.Executed, interrupt.Id, "PO-9001"), (executed.Event, executed.ApprovalId, executed.Result)));
    }

    [Fact]
    public async Task A_request_without_a_client_label_is_audited_as_unidentified()
    {
        await using var host = new AgentHost();

        await Runs.RunAsync(host.CreateClient(), Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Order a spare run capacitor for WO-1001.")));

        Assert.Equal(BeaconClient.Unidentified, Assert.Single(host.Audit).ClientId);
    }
}
