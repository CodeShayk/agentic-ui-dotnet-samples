using System.Net;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch07.Tests;

/// <summary>
/// How a turn fails. Nothing the agent or the network does escapes as an exception: each failure
/// becomes a <see cref="RunFailure"/> whose kind a UI can word, and whose <c>RunNeverStarted</c> says
/// whether sending the same input again could repeat any work. The timeouts are real ones from
/// <c>HttpClient</c> and <c>SocketsHttpHandler</c>, not hand-made exceptions.
/// </summary>
public sealed class FailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HttpClient_Timeout_before_the_first_event_is_a_timeout_that_may_have_started()
    {
        // The agent writes no headers until the model's first update, so a header timeout can't tell
        // a slow model from a dead one: the run may be executing.
        using var http = new HttpClient(new SilentHandler()) { Timeout = TimeSpan.FromMilliseconds(200) };

        var failure = await FailAsync(http);

        Assert.Equal((FailureKind.TimedOut, false), (failure.Kind, failure.RunNeverStarted));
        Assert.Equal("The agent didn't start answering in time.", failure.Message);
    }

    [Fact]
    public async Task A_connect_timeout_never_reached_the_agent()
    {
        // SocketsHttpHandler's ConnectTimeout also expires as a cancellation, but one the client can
        // tell apart: the request never left, so it's safe to send again.
        using var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            ConnectCallback = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
        })
        { Timeout = Timeout.InfiniteTimeSpan };

        var failure = await FailAsync(http);

        Assert.Equal((FailureKind.TimedOut, true), (failure.Kind, failure.RunNeverStarted));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, FailureKind.Rejected, true)]
    [InlineData(HttpStatusCode.BadRequest, FailureKind.Rejected, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FailureKind.Unreachable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, FailureKind.Unreachable, false)] // the gateway may have forwarded it
    public async Task A_status_instead_of_a_stream_is_classified_with_its_code(HttpStatusCode status, FailureKind kind, bool neverStarted)
    {
        using var http = new HttpClient(new StatusHandler(status));

        var failure = await FailAsync(http);

        Assert.Equal((kind, status, neverStarted), (failure.Kind, failure.StatusCode, failure.RunNeverStarted));
    }

    [Fact]
    public async Task A_refused_connection_never_started()
    {
        using var http = new HttpClient(new RefusingHandler());

        var failure = await FailAsync(http);

        Assert.Equal((FailureKind.Unreachable, true), (failure.Kind, failure.RunNeverStarted));
    }

    [Fact]
    public async Task A_connection_that_breaks_mid_stream_is_a_truncated_run()
    {
        var events = (await Recordings.LoadAsync("tour-tools.sse")).TakeWhile(e => e is not ToolCallResultEvent).ToList();
        var transport = new ReplayTransport(events)
        {
            FailAfter = { [0] = new IOException("Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.") },
        };
        using var harness = new Harness(transport);

        await harness.Conversation.SendAsync("Look up WO-1001", cancellationToken: Ct);

        var view = harness.View;
        Assert.Equal((ConversationStatus.Failed, FailureKind.StreamEnded, false), (view.Status, view.Failure!.Kind, view.Failure.RunNeverStarted));
        Assert.Equal(RunOutcome.Failed, view.CurrentRun!.Outcome);
        Assert.Equal(ToolCallStatus.Abandoned, view.FindToolCall("call-r2-1")!.Status);
    }

    [Fact]
    public async Task A_malformed_STATE_DELTA_fails_the_run_and_cancels_the_request()
    {
        BaseEvent[] run =
        [
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-1" },
            new StateSnapshotEvent { Snapshot = Json("""{"checklist":[]}""") },
            new StateDeltaEvent { Delta = Json("""{"op":"add","path":"/checklist/-","value":"C1"}""") }, // not an array
            new TextMessageStartEvent { MessageId = "msg-1", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg-1", Delta = "This never reaches the screen." },
        ];
        var transport = new ReplayTransport(run) { HoldOpen = true };
        using var harness = new Harness(transport);

        await harness.Conversation.SendAsync("Start the checklist", cancellationToken: Ct);
        await transport.Cancelled.Task.WaitAsync(Ct); // the request was aborted, as a Stop would abort it

        var view = harness.View;
        Assert.Equal((ConversationStatus.Failed, FailureKind.ProtocolViolation), (view.Status, view.Failure!.Kind));
        Assert.Contains("not a JSON array", view.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(RunOutcome.Failed, view.CurrentRun!.Outcome);
        Assert.Equal("""{"checklist":[]}""", view.State.Document!.ToJsonString()); // never patched, never "resynced"
        Assert.False(view.State.NeedsResync);
        Assert.Null(view.FindMessage("msg-1"));
    }

    [Fact]
    public async Task A_refused_answer_leaves_the_interrupt_open_with_the_reason()
    {
        var transport = new ReplayTransport(await Recordings.LoadAsync("tour-interrupt.sse"), [], await Recordings.LoadAsync("tour-text.sse"))
        {
            // The agent no longer holds the approval open (it expired on the server, say): a 409 before any event.
            FailBefore = { [1] = new HttpRequestException("Response status code does not indicate success: 409 (Conflict).", null, HttpStatusCode.Conflict) },
        };
        using var harness = new Harness(transport);
        await harness.Conversation.SendAsync("Order a CMP-CA36 compressor for WO-1001", cancellationToken: Ct);
        var approval = (ToolApprovalRequestContent)harness.View.FindInterrupt("approval-r10-1")!.Request!;

        await harness.Conversation.RespondAsync([approval.CreateResponse(approved: true)], cancellationToken: Ct);

        var refused = Assert.Single(harness.View.OpenInterrupts);
        Assert.Equal(("approval-r10-1", FailureKind.Rejected, HttpStatusCode.Conflict), (refused.Id, refused.LastAnswerFailure!.Kind, refused.LastAnswerFailure.StatusCode));
        Assert.Equal(ConversationStatus.Failed, harness.View.Status);

        // The user can still move the thread on: abandoning it is accepted, and it closes.
        await harness.Conversation.ContinueAsync(new RunInputs { Resume = [new AGUIResume { InterruptId = "approval-r10-1", Status = ResumeStatus.Cancelled }] }, Ct);

        var closed = harness.View.FindInterrupt("approval-r10-1")!;
        Assert.Equal((true, (RunFailure?)null), (closed.IsAnswered, closed.LastAnswerFailure));
        Assert.Equal(ConversationStatus.Idle, harness.View.Status);
    }

    [Fact]
    public async Task A_resume_stream_that_opens_with_RUN_ERROR_is_a_refusal_too()
    {
        BaseEvent[] rejected = [new RunErrorEvent { Message = "Interrupt not open.", Code = "interrupt_not_open" }];
        var transport = new ReplayTransport(await Recordings.LoadAsync("tour-interrupt.sse"), rejected);
        using var harness = new Harness(transport);
        await harness.Conversation.SendAsync("Order a CMP-CA36 compressor for WO-1001", cancellationToken: Ct);
        var approval = (ToolApprovalRequestContent)harness.View.FindInterrupt("approval-r10-1")!.Request!;

        await harness.Conversation.RespondAsync([approval.CreateResponse(approved: true)], cancellationToken: Ct);

        var refused = Assert.Single(harness.View.OpenInterrupts);
        Assert.Equal((FailureKind.AgentError, "interrupt_not_open"), (refused.LastAnswerFailure!.Kind, refused.LastAnswerFailure.Code));
    }

    [Fact]
    public async Task Answers_count_only_once_the_resuming_run_starts()
    {
        BaseEvent[] resumed =
        [
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-11" },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-11" },
        ];
        var transport = new ReplayTransport(await Recordings.LoadAsync("tour-interrupt.sse"), resumed);
        using var harness = new Harness(transport);
        await harness.Conversation.SendAsync("Order a CMP-CA36 compressor for WO-1001", cancellationToken: Ct);
        var approval = (ToolApprovalRequestContent)harness.View.FindInterrupt("approval-r10-1")!.Request!;
        var before = harness.Published.Count;

        await harness.Conversation.RespondAsync([approval.CreateResponse(approved: true)], cancellationToken: Ct);

        // Open while the request is in flight; answered in the same step that applies RUN_STARTED's update.
        var during = harness.Published.Skip(before).ToList();
        var answeredAt = during.FindIndex(p => p.Action is InterruptsAnswered);
        Assert.All(during.Take(answeredAt), p => Assert.False(p.View.FindInterrupt("approval-r10-1")!.IsAnswered));
        Assert.IsType<RunStartedEvent>(((UpdateReceived)during[answeredAt + 1].Action).Update.RawRepresentation);
    }

    private static async Task<RunFailure> FailAsync(HttpClient http)
    {
        using var client = new AGUIChatClient(new AGUIChatClientOptions(http, "http://localhost:5070/agent"));
        using var conversation = new AgentConversation(client);

        await conversation.SendAsync("What is the status of WO-1001?", cancellationToken: Ct); // never throws

        Assert.Equal(ConversationStatus.Failed, conversation.View.Status);
        Assert.Empty(conversation.View.Runs);
        return conversation.View.Failure!;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // Reads the request and never answers, like an agent whose model hasn't produced its first update.
    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("""{"title":"Refused"}""") });
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it.");
    }
}
