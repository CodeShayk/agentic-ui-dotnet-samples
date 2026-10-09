using System.Net;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Ch15.Beacon.Desk.Components.Chat;
using Ch15.Beacon.Desk.Components.Questions;
using Ch15.Forms;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace Ch15.Tests;

/// <summary>
/// The client's side, end to end against the real agent: Beacon.Client.Core's interrupt rules, answers built
/// from generated drafts, the Desk's two ways of sending them and expiry judged on the shared fake clock.
/// </summary>
public sealed class ConversationTests : IAsyncLifetime
{
    private readonly AgentHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_conversation_holds_two_open_questions_and_refuses_a_new_turn()
    {
        var (conversation, _) = _host.CreateConversation();

        await conversation.SendAsync(AgentHost.Question, cancellationToken: Ct);

        var view = conversation.View;
        Assert.Equal(ConversationStatus.AwaitingInput, view.Status);
        Assert.Equal(RunOutcome.Interrupted, view.CurrentRun!.Outcome);
        Assert.Collection(view.OpenInterrupts,
            serial => Assert.Equal((InterruptKind.Input, "input_required", "2026-11-16T16:35:00Z"), (serial.Kind, serial.Reason, serial.ExpiresAt)),
            reading => Assert.IsType<InterruptRequestContent>(reading.Request));

        await Assert.ThrowsAsync<InvalidOperationException>(() => conversation.SendAsync("Thanks", cancellationToken: Ct));
    }

    [Fact]
    public async Task Answers_built_from_generated_forms_resume_the_run_through_RespondAsync()
    {
        var (conversation, transport) = _host.CreateConversation();
        await conversation.SendAsync(AgentHost.Question, cancellationToken: Ct);

        await conversation.RespondAsync([.. conversation.View.OpenInterrupts.Select(i =>
            new InterruptResponseContent(i.Id) { Payload = Fill(i), Metadata = Wire.Json("""{"beacon":{"client":"test"}}""") })], cancellationToken: Ct);

        Assert.Equal(ConversationStatus.Idle, conversation.View.Status);
        Assert.Empty(conversation.View.OpenInterrupts);
        Assert.Contains("replace the motor: FAN-HP24", LastAnswer(conversation.View), StringComparison.Ordinal);

        // On the wire: one resume entry per interrupt, resolved, with the payload and the envelope metadata.
        var resume = transport.Runs[1].Input.Resume;
        Assert.NotNull(resume);
        Assert.Equal(2, resume.Count);
        Assert.All(resume, r => Assert.Equal((ResumeStatus.Resolved, "test"), (r.Status, r.Metadata!.Value.GetProperty("beacon").GetProperty("client").GetString())));
    }

    [Fact]
    public async Task The_Desk_answers_with_RespondAsync_and_abandons_with_a_resume_list_it_writes_itself()
    {
        var (conversation, transport) = _host.CreateConversation();
        await conversation.SendAsync(AgentHost.Question, cancellationToken: Ct);
        var (serial, reading) = (conversation.View.OpenInterrupts.First(), conversation.View.OpenInterrupts.Last());

        await InterruptAnswers.SendAsync(conversation,
            new QuestionAnswers([new QuestionAnswer(serial.Id, Fill(serial))], [reading.Id]), _host.Clock, Ct);

        var resume = transport.Runs[1].Input.Resume;
        Assert.NotNull(resume);
        Assert.Equal([(serial.Id, ResumeStatus.Resolved), (reading.Id, ResumeStatus.Cancelled)], resume.Select(r => (r.InterruptId, r.Status)));
        Assert.Null(resume[1].Payload);

        var answer = LastAnswer(conversation.View);
        Assert.Contains("is under warranty until March 30, 2031", answer, StringComparison.Ordinal);
        Assert.Contains("Without a reading I can't tell a failing motor from worn bearings", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_expiresAt_the_client_sends_nothing_but_abandoning_still_works()
    {
        var (conversation, transport) = _host.CreateConversation();
        await conversation.SendAsync(AgentHost.Question, cancellationToken: Ct);
        var open = conversation.View.OpenInterrupts.ToList();

        _host.Clock.Advance(TimeSpan.FromMinutes(16));

        await Assert.ThrowsAsync<ArgumentException>(() => conversation.RespondAsync(
            [.. open.Select(i => new InterruptResponseContent(i.Id) { Payload = Fill(i) })], cancellationToken: Ct));
        Assert.Single(transport.Runs); // refused before anything was sent

        await conversation.ContinueAsync(new RunInputs
        {
            Resume = [.. open.Select(i => new AGUIResume { InterruptId = i.Id, Status = ResumeStatus.Cancelled })],
        }, Ct);

        Assert.Equal(ConversationStatus.Idle, conversation.View.Status);
        Assert.Contains("Without the serial number I can't check the warranty", LastAnswer(conversation.View), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answers_the_agent_refuses_leave_the_questions_open_and_skipping_them_moves_the_thread_on()
    {
        // The Desk's clock is behind the agent's, so the Desk still thinks the questions are open when the agent
        // has let them expire: the agent refuses the answers with a 409 before any run starts.
        var (conversation, _) = _host.CreateConversation(clientClock: new FakeTimeProvider(AgentHost.Start));
        await conversation.SendAsync(AgentHost.Question, cancellationToken: Ct);
        var open = conversation.View.OpenInterrupts.ToList();
        _host.Clock.Advance(TimeSpan.FromMinutes(16));

        await InterruptAnswers.SendAsync(conversation, new QuestionAnswers([.. open.Select(i => new QuestionAnswer(i.Id, Fill(i)))], []), _host.Clock, Ct);

        // The turn failed as Rejected, and both questions are still open, saying why; the panel explains it.
        var view = conversation.View;
        Assert.Equal((ConversationStatus.Failed, FailureKind.Rejected), (view.Status, view.Failure!.Kind));
        Assert.Equal(2, view.OpenInterrupts.Count());
        Assert.All(view.OpenInterrupts, i => Assert.Equal(HttpStatusCode.Conflict, i.LastAnswerFailure!.StatusCode));
        Assert.Null(DeskChat.ErrorOf(view));

        // Skipping is always accepted: the run starts, the questions close and nothing is recorded.
        await InterruptAnswers.SendAsync(conversation, new QuestionAnswers([], [.. open.Select(i => i.Id)]), _host.Clock, Ct);

        Assert.Empty(conversation.View.OpenInterrupts);
        Assert.Equal(ConversationStatus.Idle, conversation.View.Status);
        Assert.Equal(0, _host.Store.EquipmentUpdates);
    }

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

    // What Sam reads off the unit, entered into each generated form.
    private static System.Text.Json.JsonElement Fill(InterruptView interrupt)
    {
        Assert.True(FormSchema.TryParse(interrupt.ResponseSchema!.Value, out var form, out _));
        var draft = new FormDraft(form);
        if (form.Field("serialNumber") is not null)
        {
            draft.SetText("serialNumber", "HP24S-2207-1184");
            draft.SetText("readFrom", "nameplate");
        }
        else
        {
            draft.SetText("amps", "2.4");
            draft.SetText("noise", "grinding");
            draft.SetFlag("shaftPlay", true);
        }

        Assert.True(draft.TryGetPayload(out var payload, out var errors), string.Join(" ", errors));
        return payload;
    }

    private static string LastAnswer(ConversationView view) =>
        view.Messages.OfType<AssistantMessageView>().Last(m => m.Text.Length > 0).Text;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
