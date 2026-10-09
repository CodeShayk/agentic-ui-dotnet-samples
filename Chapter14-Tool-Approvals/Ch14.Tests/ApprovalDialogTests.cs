using System.Net;
using System.Text.Json;
using Beacon.Client.Core;
using Bunit;
using Ch14.Beacon.Desk.Components;
using Ch14.Beacon.Desk.Components.Approvals;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ch14.Tests;

/// <summary>The Desk's approval dialog, rendered with bUnit: decisions, validation, focus, keyboard, time and refusals.</summary>
public sealed class ApprovalDialogTests : BunitContext
{
    // Dana's browser reports US Eastern time; on Beacon's Monday that is UTC-5.
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.CreateCustomTimeZone("Test/Eastern", TimeSpan.FromHours(-5), "Eastern", "Eastern");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 16, 14, 0, 0, TimeSpan.Zero));
    private readonly List<ApprovalDecision> _decisions = [];

    public ApprovalDialogTests()
    {
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton(new DispatcherTime(Eastern));
        JSInterop.Mode = JSRuntimeMode.Loose; // FocusAsync
    }

    private static ApprovalRequest Compressor { get; } = new()
    {
        Id = "apr_1",
        ToolCallId = "call-0001-1",
        ToolName = "place_part_order",
        Title = "Approve a part order",
        Message = "Order 1 x CMP-CA36 (Scroll compressor 3-ton) for WO-1001: $1,795.00. Spending over $500.00 needs a dispatcher to approve it.",
        Arguments = JsonDocument.Parse("""{"workOrderId":"WO-1001","sku":"CMP-CA36","quantity":1}""").RootElement,
        Amount = 1795.00m,
        ExpiresAt = new DateTimeOffset(2026, 11, 16, 14, 10, 0, TimeSpan.Zero),
        EditableFields = [new EditableField("quantity", "Quantity", EditableKind.Integer, "1") { Minimum = 1, Maximum = 10 }],
    };

    [Fact]
    public void The_dialog_is_labeled_by_its_title_and_described_by_the_servers_message()
    {
        var dialog = RenderDialog();

        var section = dialog.Find("[role=dialog]");
        Assert.Equal("Approve a part order", dialog.Find($"#{section.GetAttribute("aria-labelledby")}").TextContent);
        Assert.StartsWith("Order 1 x CMP-CA36", dialog.Find($"#{section.GetAttribute("aria-describedby")}").TextContent, StringComparison.Ordinal);
        Assert.Contains("$1,795.00", dialog.Find(".ch14-approval__facts").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_request_takes_focus_on_its_title()
    {
        var dialog = RenderDialog();

        var focused = JSInterop.VerifyFocusAsyncInvoke();
        Assert.Equal(dialog.Find("h3").GetAttribute("blazor:elementReference"), ((ElementReference)focused.Arguments[0]!).Id);
    }

    [Fact]
    public void Approve_raises_a_plain_approval()
    {
        var dialog = RenderDialog();

        dialog.Find("button.bd-button--primary").Click();

        Assert.Equal(new ApprovalDecision(ApprovalChoice.Approve), Assert.Single(_decisions));
    }

    [Fact]
    public void Deny_needs_a_reason_and_Escape_steps_back()
    {
        var dialog = RenderDialog();
        dialog.Find("button.bd-button--stop").Click();

        // The reason box takes focus; declining without a reason explains, in an alert, and sends nothing.
        Assert.Equal(dialog.Find("textarea").GetAttribute("blazor:elementReference"), ((ElementReference)JSInterop.VerifyFocusAsyncInvoke(calledTimes: 2)[^1].Arguments[0]!).Id);
        dialog.Find("form").Submit();
        Assert.Equal("Give a reason for declining.", dialog.Find("[role=alert]").TextContent);
        Assert.Empty(_decisions);

        dialog.Find("textarea").Change("Hold off until Priya measures the amperage.");
        dialog.Find("form").Submit();
        Assert.Equal(new ApprovalDecision(ApprovalChoice.Deny, "Hold off until Priya measures the amperage."), Assert.Single(_decisions));

        // Escape from a step goes back to the three choices.
        _decisions.Clear();
        dialog = RenderDialog();
        dialog.Find("button.bd-button--stop").Click();
        dialog.Find("[role=dialog]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(dialog.FindAll("textarea"));
        Assert.Equal("Approve", dialog.Find("button.bd-button--primary").TextContent);
    }

    [Fact]
    public void Edit_sends_the_full_arguments_with_the_change()
    {
        var dialog = RenderDialog();
        dialog.FindAll("button").Single(b => b.TextContent == "Edit…").Click();

        dialog.Find("input[type=number]").Change("11");
        dialog.Find("textarea").Change("Two units: one for the van.");
        dialog.Find("form").Submit();
        Assert.Equal("Quantity: enter a whole number from 1 to 10.", dialog.Find("[role=alert]").TextContent);

        dialog.Find("input[type=number]").Change("2");
        dialog.Find("form").Submit();

        var decision = Assert.Single(_decisions);
        Assert.Equal((ApprovalChoice.Edit, "Two units: one for the van."), (decision.Choice, decision.Reason));
        Assert.Equal("""{"workOrderId":"WO-1001","sku":"CMP-CA36","quantity":2}""", decision.EditedArguments!.ToJsonString());
    }

    [Fact]
    public void An_expired_request_can_only_be_dismissed()
    {
        _clock.Advance(TimeSpan.FromMinutes(11));
        var dialog = RenderDialog();

        Assert.Contains("expired", dialog.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        var button = Assert.Single(dialog.FindAll("button"));
        button.Click();
        Assert.Equal(new ApprovalDecision(ApprovalChoice.Dismiss), Assert.Single(_decisions));
    }

    [Fact]
    public void The_time_left_is_shown_in_the_dispatchers_zone_and_counts_down_with_a_warning_before_it_runs_out()
    {
        var dialog = RenderDialog();
        Assert.Equal("10 minutes (by 9:10 AM)", dialog.Find("time").TextContent);
        Assert.Equal(string.Empty, dialog.Find("[role=status]").TextContent);

        _clock.Advance(TimeSpan.FromMinutes(1));
        dialog.WaitForAssertion(() => Assert.Equal("9 minutes (by 9:10 AM)", dialog.Find("time").TextContent));

        // Two minutes before: a polite warning that says how to get more time.
        _clock.Advance(TimeSpan.FromMinutes(7));
        dialog.WaitForAssertion(() => Assert.StartsWith("Two minutes or less left", dialog.Find("[role=status]").TextContent, StringComparison.Ordinal));

        // At the deadline, without anyone touching the page: only Dismiss is left.
        _clock.Advance(TimeSpan.FromMinutes(2));
        dialog.WaitForAssertion(() => Assert.Equal("Dismiss", Assert.Single(dialog.FindAll("button")).TextContent));
    }

    [Fact]
    public void Until_the_agent_takes_the_answer_the_dialog_stays_and_says_it_is_waiting()
    {
        var accepted = new TaskCompletionSource();
        var dialog = Render<ApprovalDialog>(parameters => parameters
            .Add(p => p.Request, Compressor)
            .Add(p => p.OnDecided, (ApprovalDecision _) => accepted.Task));

        dialog.Find("button.bd-button--primary").Click();

        Assert.Equal("Sending your answer. It counts once Beacon starts on it.", dialog.Find("[role=status]").TextContent);
        Assert.All(dialog.FindAll("button"), b => Assert.True(b.HasAttribute("disabled")));
        accepted.SetResult();
    }

    [Fact]
    public void A_refused_answer_brings_the_choices_back_with_the_reason_and_a_way_to_dismiss()
    {
        var refused = Compressor with { LastFailure = new RunFailure(FailureKind.Rejected, "refused") { StatusCode = HttpStatusCode.Conflict } };
        var dialog = Render<ApprovalDialog>(parameters => parameters
            .Add(p => p.Request, refused)
            .Add(p => p.OnDecided, (ApprovalDecision decision) => _decisions.Add(decision)));

        Assert.StartsWith("Beacon didn't accept that answer.", dialog.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Equal(["Approve", "Edit…", "Deny…", "Dismiss", "New chat"], dialog.FindAll("button").Select(b => b.TextContent));

        dialog.FindAll("button").Single(b => b.TextContent == "Dismiss").Click();
        Assert.Equal(new ApprovalDecision(ApprovalChoice.Dismiss), Assert.Single(_decisions));
    }

    [Fact]
    public void An_approval_Beacon_already_carried_out_offers_only_a_new_chat_to_check_in()
    {
        var newChats = 0;
        var carriedOut = Compressor with
        {
            LastFailure = new RunFailure(FailureKind.Rejected, "refused") { StatusCode = HttpStatusCode.Conflict },
            LastRefusalTitle = ApprovalRequest.AlreadyAnsweredTitle,
        };
        var dialog = Render<ApprovalDialog>(parameters => parameters
            .Add(p => p.Request, carriedOut)
            .Add(p => p.OnDecided, (ApprovalDecision decision) => _decisions.Add(decision))
            .Add(p => p.OnNewChat, () => newChats++));

        Assert.Equal("Beacon already acted on this approval in an earlier run. Check the order in a new chat before asking again.",
            dialog.Find("[role=alert]").TextContent);

        // Approving again can't succeed, and nothing here invites it.
        Assert.Equal("New chat", Assert.Single(dialog.FindAll("button")).TextContent);
        dialog.Find("button").Click();
        Assert.Equal(1, newChats);
        Assert.Empty(_decisions);
    }

    private IRenderedComponent<ApprovalDialog> RenderDialog() => Render<ApprovalDialog>(parameters => parameters
        .Add(p => p.Request, Compressor)
        .Add(p => p.OnDecided, (ApprovalDecision decision) => _decisions.Add(decision)));
}
