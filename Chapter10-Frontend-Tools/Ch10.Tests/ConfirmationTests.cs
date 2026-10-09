using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Beacon.Domain;
using Ch10.Beacon.Desk.Screen;
using Ch10.Beacon.Desk.Tools;
using Microsoft.Extensions.AI;

namespace Ch10.Tests;

/// <summary>A frontend tool that changes the dispatcher's work asks her first, inside the tool, and reports her answer.</summary>
public sealed class ConfirmationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, "prefilled")]
    [InlineData(false, "declined")]
    public async Task Prefill_waits_for_the_dispatcher_and_acts_only_on_a_yes(bool confirmed, string status)
    {
        var screen = new DeskScreen();
        screen.EditDraft(new PartOrderDraft("WO-1004", "PRV-30", 2));
        var tools = new DeskTools(screen, new InMemoryFieldServiceStore());

        var prefill = tools.PrefillPartOrderAsync("WO-1001", "CAP-45-5", 1, Ct);

        // The tool is waiting, and the request shows exactly what will change, including what it replaces.
        var request = await WhenPendingAsync(screen);
        Assert.False(prefill.IsCompleted);
        Assert.Contains("Part: CAP-45-5, Dual run capacitor 45/5 uF", request.Details);
        Assert.Contains("This replaces your current draft (2 x PRV-30 for WO-1004).", request.Details);

        screen.Answer(request.Id, confirmed);
        var result = await prefill;

        Assert.Equal(status, result.Status);
        Assert.Null(screen.State.PendingConfirmation);
        Assert.Equal(confirmed ? ("WO-1001", "CAP-45-5", true) : ("WO-1004", "PRV-30", false),
            (screen.State.Draft.WorkOrderId, screen.State.Draft.Sku, screen.State.Draft.FilledByBeacon));
    }

    [Fact]
    public async Task Arguments_that_do_not_check_out_never_reach_the_dispatcher()
    {
        var screen = new DeskScreen();
        var asked = false;
        screen.Changed += (_, _) => asked |= screen.State.PendingConfirmation is not null;
        var tools = new DeskTools(screen, new InMemoryFieldServiceStore());

        var result = await tools.PrefillPartOrderAsync("WO-1001", "CMP-CA36", 50, Ct);

        Assert.Equal("not-found", result.Status);
        Assert.False(asked);
    }

    [Fact]
    public async Task An_unanswered_confirmation_times_out_and_nothing_changes()
    {
        var screen = new DeskScreen { ConfirmationTimeout = TimeSpan.FromMilliseconds(50) };
        var tools = new DeskTools(screen, new InMemoryFieldServiceStore());

        var result = await tools.PrefillPartOrderAsync("WO-1001", "CAP-45-5", 1, Ct);

        Assert.Equal("no-answer", result.Status);
        Assert.True(screen.State.Draft.IsEmpty);
        Assert.Null(screen.State.PendingConfirmation);
    }

    [Fact]
    public async Task Stop_withdraws_the_question_and_ends_the_turn_without_changing_the_form()
    {
        BaseEvent[] callsPrefill =
        [
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-1" },
            new ToolCallStartEvent { ToolCallId = "call-1", ToolCallName = DeskTools.PrefillPartOrderName, ParentMessageId = "msg-1" },
            new ToolCallArgsEvent { ToolCallId = "call-1", Delta = """{"workOrderId":"WO-1001","sku":"CAP-45-5","quantity":1}""" },
            new ToolCallEndEvent { ToolCallId = "call-1" },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-1" },
        ];
        var screen = new DeskScreen();
        var transport = new ReplayTransport(callsPrefill);
        using var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = transport });
        using var conversation = new AgentConversation(client, new ConversationOptions
        {
            ThreadId = "t-1",
            Tools = new DeskTools(screen, new InMemoryFieldServiceStore()).Create(),
        });

        var turn = conversation.SendAsync("Draft a part order for 1 x CAP-45-5 on WO-1001", cancellationToken: Ct);
        await WhenPendingAsync(screen);
        conversation.Stop();
        await turn;

        Assert.Equal(ConversationStatus.Stopped, conversation.View.Status);
        Assert.Null(screen.State.PendingConfirmation);
        Assert.True(screen.State.Draft.IsEmpty);
        Assert.Single(transport.Inputs); // no second run: the tool never produced a result
    }

    [Fact]
    public async Task A_client_side_ApprovalRequiredAIFunction_would_send_the_server_a_resume_it_never_asked_for()
    {
        // Why the Desk asks inside the tool: wrapping the tool in ApprovalRequiredAIFunction makes the
        // client's own function invocation stop with an approval request, and answering it through the
        // conversation turns into a resume entry for an interrupt the agent never raised.
        BaseEvent[] callsPrefill =
        [
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-1" },
            new ToolCallStartEvent { ToolCallId = "call-1", ToolCallName = DeskTools.PrefillPartOrderName, ParentMessageId = "msg-1" },
            new ToolCallArgsEvent { ToolCallId = "call-1", Delta = """{"workOrderId":"WO-1001","sku":"CAP-45-5","quantity":1}""" },
            new ToolCallEndEvent { ToolCallId = "call-1" },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-1" },
        ];
        BaseEvent[] anything = [new RunStartedEvent { ThreadId = "t-1", RunId = "r-2" }, new RunFinishedEvent { ThreadId = "t-1", RunId = "r-2" }];
        var transport = new ReplayTransport(callsPrefill, anything);
        var ran = false;
        var prefill = AIFunctionFactory.Create((string workOrderId, string sku, int quantity) => { ran = true; return "prefilled"; }, DeskTools.PrefillPartOrderName);
        using var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = transport });
        using var conversation = new AgentConversation(client, new ConversationOptions { ThreadId = "t-1", Tools = [new ApprovalRequiredAIFunction(prefill)] });

        await conversation.SendAsync("Draft a part order for 1 x CAP-45-5 on WO-1001", cancellationToken: Ct);

        // The agent's run finished without an interrupt; this one was made on the client's side.
        Assert.DoesNotContain(callsPrefill.OfType<RunFinishedEvent>(), e => e.Outcome is RunFinishedInterruptOutcome);
        var approval = Assert.IsType<ToolApprovalRequestContent>(Assert.Single(conversation.View.OpenInterrupts).Request);
        Assert.False(ran);

        await conversation.RespondAsync([approval.CreateResponse(approved: true)], cancellationToken: Ct);

        var resume = Assert.Single(transport.Inputs[1].Resume!);
        Assert.Equal(approval.RequestId, resume.InterruptId);
        Assert.False(ran); // and the tool still hasn't run: the "approval" went to the server
    }

    private static async Task<ConfirmationRequest> WhenPendingAsync(DeskScreen screen)
    {
        for (var i = 0; i < 200; i++)
        {
            if (screen.State.PendingConfirmation is { } request)
            {
                return request;
            }

            await Task.Delay(10, Ct);
        }

        throw new TimeoutException("The tool never asked.");
    }
}
