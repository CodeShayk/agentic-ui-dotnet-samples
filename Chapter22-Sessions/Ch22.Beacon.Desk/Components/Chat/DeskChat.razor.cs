using System.Net;
using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch22.Beacon.Desk.Sessions;
using Microsoft.AspNetCore.Components;

namespace Ch22.Beacon.Desk.Components.Chat;

/// <summary>
/// The Desk's chat. It sends, answers, regenerates and stops through the circuit's <see cref="AgentConversation"/>,
/// and refreshes the thread's run tree after every turn, since each turn adds a run to it.
/// </summary>
public sealed partial class DeskChat : IDisposable
{
    private RenderThrottle _throttle = default!;
    private ChatPanel? _panel;
    private ConversationView? _projectedFrom;
    private DeskTranscript _transcript = new([], null, new Dictionary<string, IReadOnlyList<ToolLine>>());
    private string? _error;

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    [Inject] private ConversationSessions Sessions { get; set; } = default!;

    [Inject] private TimeProvider Clock { get; set; } = default!;

    protected override void OnInitialized()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), TimeSpan.FromMilliseconds(50), Clock);
        Conversation.Changed += OnConversationChanged;
        Sessions.DocumentChanged += OnDocumentChanged;
    }

    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e) => _ = _throttle.RequestAsync();

    private void OnDocumentChanged() => _ = _throttle.RequestAsync();

    private DeskTranscript Project(ConversationView view)
    {
        if (!ReferenceEquals(view, _projectedFrom))
        {
            (_transcript, _projectedFrom) = (DeskTranscript.From(view), view);
        }

        return _transcript;
    }

    private Task SendAsync(string text) =>
        TurnAsync(() => Conversation.SendAsync(text), refocus: true, guard: !string.IsNullOrWhiteSpace(text));

    private Task RegenerateAsync(string messageId) =>
        TurnAsync(() => Sessions.RegenerateAsync(messageId), refocus: false);

    // The readings go back as the answer to the agent's interrupt; AGUIChatClient turns it into a resume entry.
    private Task AnswerAsync((string InterruptId, JsonElement Payload) answer) =>
        TurnAsync(() => Conversation.RespondAsync([new InterruptResponseContent(answer.InterruptId) { Payload = answer.Payload }]), refocus: true);

    private Task AbandonAsync(string interruptId) =>
        TurnAsync(() => Conversation.ContinueAsync(new RunInputs
        {
            Resume = [new AGUIResume { InterruptId = interruptId, Status = ResumeStatus.Cancelled }],
        }), refocus: true);

    private async Task OpenBranchAsync(string? runId)
    {
        if (runId is null || Conversation.View.ThreadId is not { } threadId || Conversation.View.IsBusy)
        {
            return;
        }

        try
        {
            _error = null;
            await Sessions.OpenAsync(threadId, runId);
        }
        catch (HttpRequestException)
        {
            _error = "Beacon's agent can't be reached, so the other version can't be opened.";
        }

        await _throttle.FlushAsync();
    }

    private async Task TurnAsync(Func<Task> turn, bool refocus, bool guard = true)
    {
        if (!guard || Conversation.View.IsBusy)
        {
            return;
        }

        _error = null;
        await turn();

        try
        {
            // The turn added a run to the thread: reload the tree for Regenerate and the versions.
            await Sessions.RefreshAsync();
        }
        catch (HttpRequestException)
        {
            // The turn itself already reported whether the agent was reachable.
        }

        await _throttle.FlushAsync();
        if (refocus && _panel is not null && Conversation.View.Status != ConversationStatus.Stopped && !Conversation.View.OpenInterrupts.Any())
        {
            await _panel.FocusComposerAsync();
        }
    }

    private void NewChat()
    {
        if (!Conversation.View.IsBusy)
        {
            _error = null;
            Sessions.New();
        }
    }

    private static ChatStatus StatusOf(ConversationView view) => view.Status switch
    {
        ConversationStatus.Starting or ConversationStatus.Running => ChatStatus.Streaming,
        ConversationStatus.Stopped => ChatStatus.Stopped,
        ConversationStatus.Failed => ChatStatus.Error,
        _ => ChatStatus.Idle,
    };

    internal static string? ErrorOf(ConversationView view) => view.Failure switch
    {
        null => null,
        { Kind: FailureKind.AgentError or FailureKind.TimedOut } failure => failure.Message,
        { Kind: FailureKind.Unreachable } => "Beacon's agent can't be reached. Check that it's running, then try again.",

        // This agent answers 409 before any run starts when the thread is busy, or no longer matches what the Desk
        // sent: another device moved it on, a question expired, or the request added nothing.
        { Kind: FailureKind.Rejected, StatusCode: HttpStatusCode.Conflict } =>
            "Beacon's agent refused the request (HTTP 409): this conversation is busy or changed elsewhere. Reopen it from the list.",
        { Kind: FailureKind.Rejected, StatusCode: { } status } =>
            $"Beacon's agent refused the request (HTTP {(int)status}). Reopen the conversation from the list if it happens again.",
        { Kind: FailureKind.ProtocolViolation } => "Beacon's answer broke a protocol rule, so the Desk stopped reading it.",
        _ => "The answer stopped unexpectedly. Try again.",
    };

    public void Dispose()
    {
        Conversation.Changed -= OnConversationChanged;
        Sessions.DocumentChanged -= OnDocumentChanged;
        _throttle.Dispose();
    }
}
