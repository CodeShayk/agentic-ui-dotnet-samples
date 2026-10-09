using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch13.Beacon.Desk.Progress;

namespace Ch13.Beacon.Desk.Components.Chat;

/// <summary>
/// The Desk's chat. It sends and stops through the circuit's <see cref="AgentConversation"/> and renders
/// the transcript projected from the view, on the same gate as the progress panels: only when the
/// transcript changed, at most every 50 milliseconds.
/// </summary>
public sealed partial class DeskChat
{
    private ChatPanel? _panel;

    /// <summary>Everything the chat panel shows. A change to any part of it is worth a render.</summary>
    public sealed record Screen(DeskTranscript Transcript, ChatStatus Status, string? ThreadId, string? RunId, string? Error);

    protected override Screen Project(ConversationView view) =>
        new(DeskTranscript.From(view), StatusOf(view), view.ThreadId, view.CurrentRun?.RunId, ErrorOf(view));

    // The transcript changes with tap events too (a message opens at TEXT_MESSAGE_START).
    protected override bool Concerns(ConversationAction action) => SliceConcerns.Any(action);

    protected override void OnInitialized()
    {
        RenderInterval = TimeSpan.FromMilliseconds(50); // text reads better at a quicker beat than the panels
        base.OnInitialized();
    }

    private async Task SendAsync(string text)
    {
        // One turn at a time, and never past an open interrupt (Chapter 7).
        var view = Conversation.View;
        if (view.IsBusy || view.OpenInterrupts.Any() || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await Conversation.SendAsync(text);

        if (Conversation.View.Status == ConversationStatus.Stopped && _panel is not null)
        {
            await _panel.FocusComposerAsync(); // Stop just became disabled; don't strand focus on it
        }
    }

    private void NewChat()
    {
        if (!Conversation.View.IsBusy)
        {
            Conversation.Reset(); // a new thread, an empty view and no shared state
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
        { Kind: FailureKind.Rejected, StatusCode: { } status } => $"Beacon's agent refused the request (HTTP {(int)status}). Start a new chat if it happens again.",
        { Kind: FailureKind.ProtocolViolation } => "Beacon's answer broke a protocol rule, so the Desk stopped reading it.",
        _ => "The answer stopped unexpectedly. Try again.",
    };
}
