using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Microsoft.AspNetCore.Components;

namespace Ch07.Beacon.Desk.Components.Chat;

/// <summary>
/// The Desk's chat. Chapter 6's version owned the history, the ChatOptions, the run and the
/// transcript; this one owns none of them. It sends and stops through the circuit's
/// <see cref="AgentConversation"/> and renders whatever view the conversation publishes.
/// </summary>
public sealed partial class DeskChat : IDisposable
{
    private RenderThrottle _throttle = default!;
    private ChatPanel? _panel;
    private ConversationView? _projectedFrom;
    private DeskTranscript _transcript = new([], null);

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    /// <summary>The shortest time between two renders while a run streams.</summary>
    [Parameter] public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    protected override void OnInitialized()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), RenderInterval);
        Conversation.Changed += OnConversationChanged;
    }

    // Raised on whichever thread changed the view, usually a thread-pool thread mid-stream. The
    // throttle renders now or once the interval ends, marshaling to the circuit through InvokeAsync,
    // and the render reads Conversation.View, which is always the latest.
    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e) => _ = _throttle.RequestAsync();

    // Projecting is cheap, but a render that asks twice for the same view gets the same lists.
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
        // One turn at a time, and never past an open interrupt: AG-UI requires every interrupt to be
        // answered or abandoned before the agent continues, and this Desk can do neither yet.
        var view = Conversation.View;
        if (view.IsBusy || view.OpenInterrupts.Any() || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await Conversation.SendAsync(text);
        await _throttle.FlushAsync();

        if (Conversation.View.Status == ConversationStatus.Stopped && _panel is not null)
        {
            await _panel.FocusComposerAsync(); // Stop just became disabled; don't strand focus on it
        }
    }

    private void NewChat()
    {
        if (!Conversation.View.IsBusy)
        {
            Conversation.Reset(); // a new thread, an empty view
        }
    }

    // The panel speaks Chapter 6's ChatStatus; the view knows more. AwaitingInput and Cancelled read as
    // ready here: Chapter 14 gives approvals a face.
    private static ChatStatus StatusOf(ConversationView view) => view.Status switch
    {
        ConversationStatus.Starting or ConversationStatus.Running => ChatStatus.Streaming,
        ConversationStatus.Stopped => ChatStatus.Stopped,
        ConversationStatus.Failed => ChatStatus.Error,
        _ => ChatStatus.Idle,
    };

    private static string? ErrorOf(ConversationView view) => view.Failure switch
    {
        null => null,
        { Kind: FailureKind.AgentError or FailureKind.TimedOut } failure => failure.Message,
        { Kind: FailureKind.Unreachable } => "Beacon's agent can't be reached. Check that it's running, then try again.",
        { Kind: FailureKind.Rejected, StatusCode: { } status } => $"Beacon's agent refused the request (HTTP {(int)status}). Start a new chat if it happens again.",
        { Kind: FailureKind.ProtocolViolation } => "Beacon's answer broke a protocol rule, so the Desk stopped reading it.",
        _ => "The answer stopped unexpectedly. Try again.",
    };

    public void Dispose()
    {
        Conversation.Changed -= OnConversationChanged;
        _throttle.Dispose();
    }
}
