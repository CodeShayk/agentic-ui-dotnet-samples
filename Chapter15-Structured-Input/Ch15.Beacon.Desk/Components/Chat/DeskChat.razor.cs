using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch15.Beacon.Desk.Components.Questions;
using Ch15.Forms;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Ch15.Beacon.Desk.Components.Chat;

/// <summary>
/// The Desk's chat. It sends and stops through the circuit's <see cref="AgentConversation"/>, renders whatever
/// view the conversation publishes and sends Dana's answers to Beacon's questions.
/// </summary>
public sealed partial class DeskChat : IDisposable
{
    private RenderThrottle _throttle = default!;
    private ChatPanel? _panel;
    private ConversationView? _projectedFrom;
    private DeskTranscript _transcript = new([], null);

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    [Inject] private TimeProvider Clock { get; set; } = default!;

    [Inject] private SentAnswers Sent { get; set; } = default!;

    [Inject] private DispatcherTime Time { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>The shortest time between two renders while a run streams.</summary>
    [Parameter] public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    protected override void OnInitialized()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), RenderInterval, Clock);
        Conversation.Changed += OnConversationChanged;
    }

    // Once per circuit, as soon as there is a browser to ask: answer-by times show in Dana's own time zone.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && RendererInfo.IsInteractive)
        {
            await Time.DetectAsync(JS);
        }
    }

    // Raised on whichever thread changed the view; the throttle marshals to the circuit and renders the latest.
    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e) => _ = _throttle.RequestAsync();

    private DeskTranscript Project(ConversationView view)
    {
        if (!ReferenceEquals(view, _projectedFrom))
        {
            (_transcript, _projectedFrom) = (DeskTranscript.From(view), view);
        }

        return _transcript;
    }

    private async Task SendAsync(string text)
    {
        var view = Conversation.View;
        if (view.IsBusy || view.OpenInterrupts.Any() || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await Conversation.SendAsync(text);
        await _throttle.FlushAsync();
    }

    private async Task AnswerAsync(QuestionAnswers answers)
    {
        Sent.Record(answers);

        try
        {
            await InterruptAnswers.SendAsync(Conversation, answers, Clock);
        }
        catch (ArgumentException)
        {
            // A question expired between the click and the send. The conversation refused before sending
            // anything, so the questions are still open and the panel shows the expired one as skip-only.
            Sent.Forget(answers);
            return;
        }

        await _throttle.FlushAsync();

        // Accepted: the panel is gone, and focus goes back to the composer. Not accepted: the questions are still
        // open (LastAnswerFailure says why), and the panel offers to answer them again or skip them.
        if (!Conversation.View.OpenInterrupts.Any() && _panel is not null)
        {
            await _panel.FocusComposerAsync();
        }
    }

    private void NewChat()
    {
        if (!Conversation.View.IsBusy)
        {
            Sent.Clear();
            Conversation.Reset();
        }
    }

    // From the panel, once Beacon has refused the answers: a new thread, and focus where Dana types.
    private async Task StartOverAsync()
    {
        NewChat();
        await _throttle.FlushAsync();
        if (_panel is not null)
        {
            await _panel.FocusComposerAsync();
        }
    }

    private static ChatStatus StatusOf(ConversationView view) => view.Status switch
    {
        ConversationStatus.Starting or ConversationStatus.Running => ChatStatus.Streaming,
        ConversationStatus.Stopped => ChatStatus.Stopped,
        ConversationStatus.Failed => ChatStatus.Error,
        _ => ChatStatus.Idle,
    };

    // Refused answers are explained in the question panel, which stays open; the status bar covers the rest.
    internal static string? ErrorOf(ConversationView view) => view.Failure switch
    {
        null => null,
        { } when view.OpenInterrupts.Any(i => i.LastAnswerFailure is not null) => null,
        { Kind: FailureKind.AgentError or FailureKind.TimedOut } failure => failure.Message,
        { Kind: FailureKind.Unreachable } => "Beacon's agent can't be reached. Check that it's running, then try again.",
        { Kind: FailureKind.Rejected, StatusCode: { } status } => $"Beacon's agent refused the request (HTTP {(int)status}). Start a new chat if it happens again.",
        _ => "The answer stopped unexpectedly. Try again.",
    };

    public void Dispose()
    {
        Conversation.Changed -= OnConversationChanged;
        _throttle.Dispose();
    }
}
