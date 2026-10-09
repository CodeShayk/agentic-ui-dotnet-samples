using System.Net;
using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch13.Beacon.Desk.Components.Chat;
using Ch13.Beacon.Desk.Progress;
using Ch13.Tests.Infrastructure;
using Microsoft.Extensions.AI;

namespace Ch13.Tests;

/// <summary>The Desk's side: what the client enforces, and how few renders the progression needs.</summary>
public sealed class ClientTests
{
    [Fact]
    public async Task A_step_left_open_at_RUN_FINISHED_fails_the_turn()
    {
        BaseEvent[] run =
        [
            new RunStartedEvent { ThreadId = "t-13", RunId = "r-1" },
            new StepStartedEvent { StepName = "investigating" },
            new RunFinishedEvent { ThreadId = "t-13", RunId = "r-1" },
        ];
        using var harness = new Harness(new ReplayTransport(run));

        await harness.Conversation.SendAsync(new ChatMessage(ChatRole.User, "Hello") { MessageId = "user-1" }, cancellationToken: TestContext.Current.CancellationToken);

        // AGUIChatClient checks step pairing and throws; the conversation reports a broken stream.
        Assert.Equal(ConversationStatus.Failed, harness.Conversation.View.Status);
        Assert.Equal(FailureKind.ProtocolViolation, harness.Conversation.View.Failure!.Kind);
        Assert.Contains("steps are still active", harness.Conversation.View.Failure.Message, StringComparison.Ordinal);
        Assert.Equal("Beacon's answer broke a protocol rule, so the Desk stopped reading it.", DeskChat.ErrorOf(harness.Conversation.View));
    }

    [Theory]
    [InlineData(FailureKind.Rejected, HttpStatusCode.Conflict, "Beacon's agent refused the request (HTTP 409). Start a new chat if it happens again.")]
    [InlineData(FailureKind.Rejected, HttpStatusCode.BadRequest, "Beacon's agent refused the request (HTTP 400). Start a new chat if it happens again.")]
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
    public async Task An_activity_delta_before_its_snapshot_is_skipped_with_a_warning()
    {
        using var patch = JsonDocument.Parse("""[{"op":"replace","path":"/checks/0/status","value":"done"}]""");
        BaseEvent[] run =
        [
            new RunStartedEvent { ThreadId = "t-13", RunId = "r-1" },
            new ActivityDeltaEvent { MessageId = "checks-r-1", ActivityType = "beacon.checks", Patch = patch.RootElement.Clone() },
            new RunFinishedEvent { ThreadId = "t-13", RunId = "r-1" },
        ];
        using var harness = new Harness(new ReplayTransport(run));

        await harness.Conversation.SendAsync(new ChatMessage(ChatRole.User, "Hello") { MessageId = "user-1" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ConversationStatus.Idle, harness.Conversation.View.Status); // never fatal
        Assert.Contains(harness.Conversation.View.Warnings, w => w.Contains("checks-r-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_panel_renders_only_when_its_own_projection_changes()
    {
        var reportRenders = 0;
        var planRenders = 0;
        SliceRenderGate<ReportView?>? report = null;
        SliceRenderGate<PlanView?>? plan = null;

        using var harness = await Harness.ReplayAsync(before: h =>
        {
            // A zero interval renders every change that passes the gate, so each pass is one render.
            report = new SliceRenderGate<ReportView?>(() => h.Conversation.View, ReportView.From, SliceConcerns.Progress,
                new RenderThrottle(() => { reportRenders++; return Task.CompletedTask; }, TimeSpan.Zero));
            plan = new SliceRenderGate<PlanView?>(() => h.Conversation.View, PlanView.From, SliceConcerns.Progress,
                new RenderThrottle(() => { planRenders++; return Task.CompletedTask; }, TimeSpan.Zero));
            h.Conversation.Changed += (_, e) =>
            {
                _ = report.OnChangedAsync(e);
                _ = plan.OnChangedAsync(e);
            };
        });

        var changes = harness.Published.Count;
        var distinctReports = harness.Published.Select(p => ReportView.From(p.View)).Distinct().Count();
        var distinctPlans = harness.Published.Select(p => PlanView.From(p.View)).Distinct().Count();

        // One render per distinct value after the first (the panel rendered its initial value on creation).
        Assert.Equal(distinctReports - 1, reportRenders);
        Assert.Equal(distinctPlans - 1, planRenders);
        Assert.True(planRenders < 15, $"the plan rendered {planRenders} times");
        Assert.True(changes > 4 * reportRenders, $"{changes} changes, {reportRenders} report renders");
        Assert.Equal(ReportStatus.Saved, report!.Current!.Status);
        Assert.All(plan!.Current!.Steps, s => Assert.Equal(PlanStepStatus.Done, s.Status));
    }
}
