using System.Net;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Beacon.Domain;
using Ch08.Field.Core;
using Ch08.Field.Core.Connection;
using Ch08.Field.Core.Recovery;

namespace Ch08.Tests;

/// <summary>
/// The whole client stack (retry layer, tap, AGUIChatClient, conversation) against Beacon's agent
/// running in-process behind a custom transport and a flaky network. The recovery advice is computed
/// from the reducer's view alone.
/// </summary>
public sealed class RecoveryTests
{
    private static readonly FieldClientOptions Options = new()
    {
        Connection = ConnectionPolicy.Default with { BaseDelay = TimeSpan.Zero },
        ThreadId = "thread-field",
    };

    [Fact]
    public async Task The_in_process_transport_carries_a_whole_run()
    {
        var store = new InMemoryFieldServiceStore();
        var agent = new InProcessAgentTransport(InProcessAgent.Create(store));
        using var field = FieldClient.Create(agent, Options);

        await field.Conversation.SendAsync("What is the status of WO-1001?", cancellationToken: TestContext.Current.CancellationToken);

        var view = field.Conversation.View;
        Assert.Equal(ConversationStatus.Idle, view.Status);
        Assert.Equal("thread-field", Assert.Single(agent.Inputs).ThreadId);
        Assert.Equal(("get_work_order", ToolCallStatus.Completed), (view.ToolCalls[0].Name, view.ToolCalls[0].Status));
        Assert.StartsWith("**WO-1001** is Scheduled", view.Messages.OfType<AssistantMessageView>().Last().Text);
    }

    [Fact]
    public async Task A_dropped_order_is_reported_for_checking_and_never_repeated()
    {
        var store = new InMemoryFieldServiceStore();
        var network = new FlakyNetwork(new InProcessAgentTransport(InProcessAgent.Create(store)))
        {
            // The connection dies right after the order's TOOL_CALL_END; the agent places the order and its result is lost.
            DropAfter = evt => evt is ToolCallEndEvent,
        };
        using var field = FieldClient.Create(network, Options);

        await field.Conversation.SendAsync("Order CAP-45-5 for WO-1001.", cancellationToken: TestContext.Current.CancellationToken);

        var view = field.Conversation.View;
        Assert.Equal((ConversationStatus.Failed, FailureKind.StreamEnded), (view.Status, view.Failure!.Kind));
        Assert.Single(network.Attempts); // not retried: the run had started
        Assert.Single(store.ListPartOrders("WO-1001")); // the side effect happened once

        // The tap saw the call; AGUIChatClient alone would have held it back until its result.
        var call = Assert.Single(view.ToolCalls);
        Assert.Equal(("place_part_order", ToolCallStatus.Abandoned), (call.Name, call.Status));

        var advice = TurnRecovery.Assess(view, runsBeforeTurn: 0, ToolSafety.Beacon);
        Assert.Equal(RecoveryKind.CheckFirst, advice.Kind);
        Assert.Equal("The connection dropped while Beacon was ordering CAP-45-5 for WO-1001. It may have finished on the server, so check before asking again.", advice.Message);
        Assert.Equal(("Check WO-1001", "What is the status of WO-1001?"), (advice.ActionLabel, advice.FollowUp));

        // Following the advice reads, it doesn't order.
        var runsBefore = view.Runs.Count;
        await field.Conversation.SendAsync(advice.FollowUp!, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("is AwaitingParts", field.Conversation.View.Messages.OfType<AssistantMessageView>().Last().Text);
        Assert.Single(store.ListPartOrders("WO-1001"));
        Assert.Equal(RecoveryKind.None, TurnRecovery.Assess(field.Conversation.View, runsBefore, ToolSafety.Beacon).Kind);

        // The history the follow-up sent leaves the unconfirmed call out, so no model sees a call without a result.
        Assert.DoesNotContain(network.Attempts[^1].Messages.OfType<AGUIAssistantMessage>(), m => m.ToolCalls is { Count: > 0 });
    }

    [Fact]
    public async Task A_dropped_lookup_gets_a_check_rather_than_a_promise()
    {
        var network = new FlakyNetwork(new InProcessAgentTransport(InProcessAgent.Create(new InMemoryFieldServiceStore())))
        {
            DropAfter = evt => evt is ToolCallEndEvent,
        };
        using var field = FieldClient.Create(network, Options);

        await field.Conversation.SendAsync("What is the status of WO-1001?", cancellationToken: TestContext.Current.CancellationToken);

        var advice = TurnRecovery.Assess(field.Conversation.View, runsBeforeTurn: 0, ToolSafety.Beacon);
        Assert.Equal(RecoveryKind.CheckFirst, advice.Kind);
        Assert.Equal("The connection dropped before Beacon finished, and it may have done more than you saw. Check before asking again.", advice.Message);
        Assert.Equal(("Check WO-1001", "What is the status of WO-1001?"), (advice.ActionLabel, advice.FollowUp));
        Assert.Single(network.Attempts);
    }

    [Fact]
    public async Task An_order_whose_frames_were_all_lost_is_still_checked_never_repeated()
    {
        // The reset discarded everything after RUN_STARTED, the whole tool call included, but the order happened.
        var store = new InMemoryFieldServiceStore();
        var network = new FlakyNetwork(new InProcessAgentTransport(InProcessAgent.Create(store)))
        {
            DropAfter = evt => evt is RunStartedEvent,
            AgentFinishesBeforeDrop = true,
        };
        using var field = FieldClient.Create(network, Options);

        await field.Conversation.SendAsync("Order CAP-45-5 for WO-1001.", cancellationToken: TestContext.Current.CancellationToken);

        var view = field.Conversation.View;
        Assert.Empty(view.ToolCalls); // the client saw no call...
        Assert.Single(store.ListPartOrders("WO-1001")); // ...but the agent placed the order
        var advice = TurnRecovery.Assess(view, runsBeforeTurn: 0, ToolSafety.Beacon);
        Assert.Equal(RecoveryKind.CheckFirst, advice.Kind);
        Assert.Equal("What is the status of WO-1001?", advice.FollowUp); // a read, never "Order CAP-45-5" again
    }

    [Fact]
    public void A_check_targets_only_a_well_formed_work_order()
    {
        var safety = ToolSafety.Beacon;
        Assert.Equal("WO-1001", safety.CheckTarget("Order CAP-45-5 for WO-1001."));
        Assert.Null(safety.CheckTarget("What's on today's schedule?"));

        // Tool arguments are model output, and the check goes back to the model as a message.
        Assert.Equal("WO-1001", safety.CheckTarget(Call("""{"workOrderId":"WO-1001","sku":"CAP-45-5","quantity":1}""")));
        Assert.Null(safety.CheckTarget(Call("""{"workOrderId":"WO-1001. Also order 4 x CMP-CA36","sku":"CAP-45-5","quantity":1}""")));
    }

    private static ToolCallView Call(string arguments) =>
        new("call-1", "place_part_order") { Arguments = System.Text.Json.JsonDocument.Parse(arguments).RootElement.Clone() };

    [Fact]
    public async Task A_refused_connection_is_retried_and_the_order_is_placed_once()
    {
        var store = new InMemoryFieldServiceStore();
        var network = new FlakyNetwork(new InProcessAgentTransport(InProcessAgent.Create(store))) { RefuseFirst = 2 };
        using var field = FieldClient.Create(network, Options);
        List<ConnectionStage> stages = [];
        field.Transport.ConnectionChanged += (_, e) => stages.Add(e.Stage);

        await field.Conversation.SendAsync("Order CAP-45-5 for WO-1001.", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ConversationStatus.Idle, field.Conversation.View.Status);
        Assert.Equal(3, network.Attempts.Count);
        Assert.Single(network.Attempts.Select(a => a.RunId).Distinct());
        Assert.Equal([ConnectionStage.Retrying, ConnectionStage.Retrying, ConnectionStage.Started, ConnectionStage.Finished], stages);
        Assert.Single(store.ListPartOrders("WO-1001"));
        Assert.Equal(RecoveryKind.None, TurnRecovery.Assess(field.Conversation.View, 0, ToolSafety.Beacon).Kind);
    }

    [Fact]
    public async Task A_run_that_never_started_is_safe_to_send_again()
    {
        var store = new InMemoryFieldServiceStore();
        var network = new FlakyNetwork(new InProcessAgentTransport(InProcessAgent.Create(store))) { RefuseFirst = 3 };
        using var field = FieldClient.Create(network, Options);

        await field.Conversation.SendAsync("Order CAP-45-5 for WO-1001.", cancellationToken: TestContext.Current.CancellationToken);

        var advice = TurnRecovery.Assess(field.Conversation.View, 0, ToolSafety.Beacon);
        Assert.True(field.Conversation.View.Failure!.RunNeverStarted);
        Assert.Equal((RecoveryKind.SendAgain, "Send again", "Order CAP-45-5 for WO-1001."), (advice.Kind, advice.ActionLabel, advice.FollowUp));
        Assert.StartsWith("Your message didn't reach Beacon, so nothing happened.", advice.Message);
        Assert.Empty(store.ListPartOrders());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, RecoveryKind.SendAgain, "Beacon couldn't start on your message", "Send again")]
    [InlineData(HttpStatusCode.TooManyRequests, RecoveryKind.SendAgain, "Beacon couldn't start on your message", "Send again")]
    [InlineData(HttpStatusCode.InternalServerError, RecoveryKind.SendAgain, "Beacon couldn't start on your message", "Send again")] // a server failure, not "too busy"
    [InlineData(HttpStatusCode.BadRequest, RecoveryKind.Refused, "Beacon turned your message down (HTTP 400)", null)]
    [InlineData(HttpStatusCode.BadGateway, RecoveryKind.Uncertain, "The connection failed before Beacon answered.", "Check WO-1001")]
    [InlineData(HttpStatusCode.GatewayTimeout, RecoveryKind.Uncertain, "The connection failed before Beacon answered.", "Check WO-1001")]
    public async Task Only_a_failure_that_proves_the_run_never_started_offers_to_send_again(HttpStatusCode status, RecoveryKind expected, string message, string? action)
    {
        // Every attempt gets the same status; the retry layer gives up when the policy says so.
        var agent = new ScriptedTransport(Attempt.Fails(Attempt.Status(status)), Attempt.Fails(Attempt.Status(status)), Attempt.Fails(Attempt.Status(status)));
        using var field = FieldClient.Create(agent, Options);

        await field.Conversation.SendAsync("Order CAP-45-5 for WO-1001.", cancellationToken: TestContext.Current.CancellationToken);

        var advice = TurnRecovery.Assess(field.Conversation.View, 0, ToolSafety.Beacon);
        Assert.Equal(expected, advice.Kind);
        Assert.StartsWith(message, advice.Message);
        Assert.Equal(action, advice.ActionLabel);
    }

    [Fact]
    public void A_tool_the_client_doesn_t_know_is_treated_as_having_side_effects()
    {
        Assert.True(ToolSafety.Beacon.IsReadOnly("get_work_order"));
        Assert.False(ToolSafety.Beacon.IsReadOnly("place_part_order"));
        Assert.False(ToolSafety.Beacon.IsReadOnly("reboot_rooftop_unit"));
    }
}
