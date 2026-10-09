using System.Net;
using System.Text.Json.Nodes;
using Beacon.Client.Core;
using Ch12.Beacon.Desk.Components.Chat;

namespace Ch12.Tests;

/// <summary>How Beacon Desk words a turn that failed, starting with the agent refusing the sheet.</summary>
public sealed class DeskFailureTests
{
    [Fact]
    public async Task A_sheet_the_agent_refuses_fails_the_turn_as_rejected_before_any_run_starts()
    {
        await using var agent = new AgentHost();
        using var desk = new DeskStack(agent.CreateClient());
        await desk.SendAsync("Open WO-1001");

        // A copy of C1 under the same ID: the schema accepts it, the rule checks don't.
        var tampered = desk.Document!.DeepClone();
        tampered["workOrder"]!["checklist"]!.AsArray().Add(JsonNode.Parse("""{"id":"C1","text":"A second C1","done":false}"""));
        desk.Conversation.UpdateState(tampered);
        var runs = desk.Conversation.View.Runs.Count;

        await desk.SendAsync("What is the status of WO-1001?");

        var view = desk.Conversation.View;
        Assert.Equal(ConversationStatus.Failed, view.Status);
        Assert.Equal((FailureKind.Rejected, HttpStatusCode.BadRequest, true), (view.Failure!.Kind, view.Failure.StatusCode, view.Failure.RunNeverStarted));
        Assert.Equal(runs, view.Runs.Count); // no RUN_STARTED: the agent refused the input
        Assert.Equal(
            "Beacon's agent refused the sheet this Desk sent (HTTP 400). Start a new chat and open the work order again.",
            DeskChat.ErrorOf(view));
    }

    [Theory]
    [InlineData(FailureKind.Rejected, HttpStatusCode.Conflict, "Beacon's agent refused the request (HTTP 409). Start a new chat if it happens again.")]
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
