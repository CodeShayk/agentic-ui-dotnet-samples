using System.Net;
using Beacon.Client.Core;
using Ch22.Beacon.Desk.Components.Chat;
using Ch22.Tests.Infrastructure;

namespace Ch22.Tests;

/// <summary>How Beacon Desk words a turn that failed, starting with the agent refusing a run before it starts.</summary>
public sealed class DeskFailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_the_agent_refuses_fails_the_turn_as_rejected_before_any_run_starts()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path);
        using var desk = new DeskStack(agent, "t-refused");
        await desk.Conversation.SendAsync("What is the status of WO-1005?", cancellationToken: Ct);
        var runs = desk.View.Runs.Count;

        // A run that adds nothing to the conversation: the agent answers 409 before it starts one.
        await desk.Conversation.ContinueAsync(cancellationToken: Ct);

        var view = desk.View;
        Assert.Equal(ConversationStatus.Failed, view.Status);
        Assert.Equal((FailureKind.Rejected, HttpStatusCode.Conflict, true), (view.Failure!.Kind, view.Failure.StatusCode, view.Failure.RunNeverStarted));
        Assert.Equal(runs, view.Runs.Count); // no RUN_STARTED: the agent refused the input
        Assert.Equal(
            "Beacon's agent refused the request (HTTP 409): this conversation is busy or changed elsewhere. Reopen it from the list.",
            DeskChat.ErrorOf(view));
    }

    [Theory]
    [InlineData(FailureKind.Rejected, HttpStatusCode.BadRequest, "Beacon's agent refused the request (HTTP 400). Reopen the conversation from the list if it happens again.")]
    [InlineData(FailureKind.Unreachable, HttpStatusCode.ServiceUnavailable, "Beacon's agent can't be reached. Check that it's running, then try again.")]
    [InlineData(FailureKind.ProtocolViolation, null, "Beacon's answer broke a protocol rule, so the Desk stopped reading it.")]
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
}
