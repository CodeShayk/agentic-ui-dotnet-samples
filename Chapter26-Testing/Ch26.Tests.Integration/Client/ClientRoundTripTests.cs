using AGUI.Client;
using Beacon.Client.Core;
using Ch26.Tests.Integration.Infrastructure;
using Microsoft.Extensions.AI;

namespace Ch26.Tests.Integration.Client;

/// <summary>
/// The real client against the real server, in one process: Beacon Desk's stack (WireEventTap,
/// AGUIChatClient, AgentConversation) talking to Ch26.Beacon.Agent over the in-memory test server. The
/// replay tests in Ch26.Tests.Unit can't see a server change; these can.
/// </summary>
public sealed class ClientRoundTripTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_desk_sees_the_work_order_the_run_opened()
    {
        await using var agent = await AgentHost.ReplayingAsync(Recordings.Status);
        using var conversation = Connect(agent);

        await conversation.SendAsync("What is the status of WO-1001?", cancellationToken: Ct);

        var view = conversation.View;
        Assert.Null(view.Failure);
        Assert.Equal(ConversationStatus.Idle, view.Status);
        Assert.Equal("WO-1001", view.State.Document?["activeWorkOrder"]?["id"]?.GetValue<string>());
        Assert.Contains("Harbor View Dental", view.Messages.OfType<AssistantMessageView>().Last().Text);
    }

    [Fact]
    public async Task Dana_approves_the_compressor_and_the_order_is_placed()
    {
        await using var agent = await AgentHost.ReplayingAsync(Recordings.Order, Recordings.OrderApproved);
        using var conversation = Connect(agent);

        await conversation.SendAsync("Order a replacement compressor CMP-CA36 for WO-1001.", cancellationToken: Ct);
        var approval = Assert.Single(conversation.View.OpenInterrupts);
        Assert.Equal(ConversationStatus.AwaitingInput, conversation.View.Status);

        var request = Assert.IsType<ToolApprovalRequestContent>(approval.Request);
        await conversation.RespondAsync([request.CreateResponse(approved: true)], cancellationToken: Ct);

        var view = conversation.View;
        Assert.Null(view.Failure);
        Assert.Empty(view.OpenInterrupts);
        Assert.StartsWith("Order PO-9001 is placed", view.Messages.OfType<AssistantMessageView>().Last().Text);
    }

    private static AgentConversation Connect(AgentHost agent)
    {
        var http = agent.CreateClient();
        var tap = WireEventTap.ForHttp(http, new Uri(http.BaseAddress!, "/agent").ToString());
        return new AgentConversation(
            new AGUIChatClient(new AGUIChatClientOptions { Transport = tap }),
            new ConversationOptions { Tap = tap });
    }
}
