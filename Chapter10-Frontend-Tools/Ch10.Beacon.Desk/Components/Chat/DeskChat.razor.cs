using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch10.Beacon.Desk.Screen;
using Microsoft.AspNetCore.Components;

namespace Ch10.Beacon.Desk.Components.Chat;

/// <summary>
/// The Desk's chat. It sends and stops through the circuit's <see cref="AgentConversation"/>, renders
/// whatever view the conversation publishes and shows the <see cref="DeskScreen"/>'s pending confirmation.
/// </summary>
public sealed partial class DeskChat : IDisposable
{
    private RenderThrottle _throttle = default!;
    private ChatPanel? _panel;
    private ConversationView? _projectedFrom;
    private DeskTranscript _transcript = new([], null);

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    [Inject] private DeskScreen Screen { get; set; } = default!;

    /// <summary>The shortest time between two renders while a run streams.</summary>
    [Parameter] public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    protected override void OnInitialized()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), RenderInterval);
        Conversation.Changed += OnConversationChanged;
        Screen.Changed += OnScreenChanged;
    }

    // Both are raised on whichever thread made the change: mid-stream, or inside a tool. The throttle
    // marshals to the circuit through InvokeAsync and renders the latest view.
    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e) => _ = _throttle.RequestAsync();

    private void OnScreenChanged(object? sender, EventArgs e) => _ = _throttle.RequestAsync();

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

        if (Conversation.View.Status == ConversationStatus.Stopped && _panel is not null)
        {
            await _panel.FocusComposerAsync(); // Stop just became disabled; don't strand focus on it
        }
    }

    // The tool that asked is awaiting this answer on another thread; answering completes it, the turn
    // continues and the card disappears. Focus goes back to the composer, where Dana was before Beacon asked.
    private async Task AnswerAsync(ConfirmationRequest request, bool confirmed)
    {
        Screen.Answer(request.Id, confirmed);
        if (_panel is not null)
        {
            await _panel.FocusComposerAsync();
        }
    }

    private void NewChat()
    {
        if (!Conversation.View.IsBusy)
        {
            Conversation.Reset();
        }
    }

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
        Screen.Changed -= OnScreenChanged;
        _throttle.Dispose();
    }
}
