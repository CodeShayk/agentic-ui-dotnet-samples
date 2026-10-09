using System.Net;
using System.Text.Json;
using Beacon.Client.Core;
using Bunit;
using Ch14.Beacon.Desk.Components;
using Ch14.Beacon.Desk.Components.Approvals;
using Ch14.Beacon.Desk.Components.Chat;
using Microsoft.JSInterop;

namespace Ch14.Tests;

/// <summary>How the Desk words failures, sends a reason, and tells the time in the dispatcher's own zone.</summary>
public sealed class DeskRecoveryTests : BunitContext
{
    private static readonly DateTimeOffset Deadline = new(2026, 11, 16, 15, 13, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(FailureKind.Rejected, HttpStatusCode.Conflict, "Beacon's agent refused the request (HTTP 409). Start a new chat if it happens again.")]
    [InlineData(FailureKind.Unreachable, HttpStatusCode.ServiceUnavailable, "Beacon's agent can't be reached. Check that it's running, then try again.")]
    [InlineData(FailureKind.StreamEnded, null, "The answer stopped unexpectedly. Try again.")]
    public void Each_kind_of_failure_has_its_own_words(FailureKind kind, HttpStatusCode? status, string expected)
    {
        var view = ConversationView.Empty with
        {
            Status = ConversationStatus.Failed,
            Failure = new RunFailure(kind, "details") { StatusCode = status },
        };

        Assert.Equal(expected, DeskChat.ErrorOf(view));
    }

    [Fact]
    public void A_reason_typed_on_two_lines_goes_to_the_agent_on_one()
    {
        var request = new ApprovalRequest
        {
            Id = "apr_1",
            ToolCallId = "call-0001-1",
            ToolName = "place_part_order",
            Title = "Approve a part order",
            Message = "Order 1 x CMP-CA36 for WO-1001: $1,795.00.",
            Arguments = JsonDocument.Parse("""{"workOrderId":"WO-1001","sku":"CMP-CA36","quantity":1}""").RootElement,
        };

        var entry = ApprovalAnswers.ToResume(request, new ApprovalDecision(ApprovalChoice.Deny, "Hold off.\r\nMeasure the amperage first."));

        Assert.Equal("Hold off. Measure the amperage first.", entry.Payload!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_dispatchers_time_zone_comes_from_her_browser()
    {
        // Intl.DateTimeFormat().resolvedOptions().timeZone, as a browser in New York answers it.
        var format = JSInterop.SetupModule("Intl.DateTimeFormat", []);
        format.Setup<JsonElement>("resolvedOptions").SetResult(JsonDocument.Parse("""{"locale":"en-US","timeZone":"America/New_York"}""").RootElement);
        var time = new DispatcherTime();

        await time.DetectAsync(JSInterop.JSRuntime);

        Assert.NotNull(time.Zone);
        Assert.Equal("10:13 AM", time.ClockTime(Deadline));
        Assert.Equal("9 minutes (by 10:13 AM)", time.TimeLeft(Deadline, Deadline.AddMinutes(-8.5)));
    }

    [Fact]
    public async Task Without_a_browser_to_ask_times_stay_in_UTC_and_say_so()
    {
        var time = new DispatcherTime();

        await time.DetectAsync(new PrerenderingJSRuntime());

        Assert.Null(time.Zone);
        Assert.Equal("15:13 UTC", time.ClockTime(Deadline));
    }

    [Theory]
    [InlineData(600, 60)]   // 10:00 left: the count changes to 9 in a minute
    [InlineData(570, 30)]   // 9:30 left ("10 minutes"): it changes at 9:00
    [InlineData(20, 20)]    // 0:20 left: the next change is the deadline itself
    public void A_countdown_renders_again_when_the_minutes_left_change(int secondsLeft, int expectedDelaySeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedDelaySeconds), DispatcherTime.UntilNextChange(Deadline, Deadline.AddSeconds(-secondsLeft)));
        Assert.Equal(Timeout.InfiniteTimeSpan, DispatcherTime.UntilNextChange(Deadline, Deadline));
    }

    // What Blazor's runtime does while a component prerenders: there is no browser yet.
    private sealed class PrerenderingJSRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("JavaScript interop calls cannot be issued at this time.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("JavaScript interop calls cannot be issued at this time.");
    }
}
