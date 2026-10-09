using System.Net;
using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch17.Media;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;

namespace Ch17.Beacon.Desk.Components.Chat;

/// <summary>The Desk's chat: it sends messages with attachments and renders the conversation's view.</summary>
public sealed partial class MediaChat : IDisposable
{
    private RenderThrottle _throttle = default!;
    private AttachmentComposer? _composer;
    private ConversationView? _projectedFrom;
    private DeskTranscript _transcript = new([], null);

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    protected override void OnInitialized()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), TimeSpan.FromMilliseconds(50));
        Conversation.Changed += OnConversationChanged;
    }

    // Raised on whichever thread made the change; the throttle marshals to the circuit.
    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e) => _ = _throttle.RequestAsync();

    private DeskTranscript Project(ConversationView view)
    {
        if (!ReferenceEquals(view, _projectedFrom))
        {
            (_transcript, _projectedFrom) = (DeskTranscript.From(view), view);
        }

        return _transcript;
    }

    // Every attachment already in the conversation goes out again with each new message.
    private static long ConversationBytes(ConversationView view) =>
        MediaAttachment.BytesIn(view.Messages.OfType<UserMessageView>().SelectMany(u => u.Attachments));

    private async Task SendAsync(ChatMessage message)
    {
        if (Conversation.View.IsBusy)
        {
            return;
        }

        await Conversation.SendAsync(message);
        await _throttle.FlushAsync();

        if (Conversation.View.Status == ConversationStatus.Stopped && _composer is not null)
        {
            await _composer.FocusAsync(); // Stop just became disabled; don't strand focus on it
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

    // A refused request stays in the conversation and would be refused again on the next turn, so a
    // refusal points Dana to a new chat.
    internal static string? ErrorOf(ConversationView view) => view.Failure switch
    {
        null => null,
        { Kind: FailureKind.AgentError or FailureKind.TimedOut } failure => failure.Message,
        { Kind: FailureKind.Unreachable } => "Beacon's agent can't be reached. Check that it's running, then try again.",
        { Kind: FailureKind.Rejected, StatusCode: HttpStatusCode.RequestEntityTooLarge } =>
            "The conversation's attachments are too large for Beacon's agent (HTTP 413). Start a new chat to send more.",
        { Kind: FailureKind.Rejected, StatusCode: { } status } =>
            $"Beacon's agent refused the request (HTTP {(int)status}). Start a new chat if it happens again.",
        { Kind: FailureKind.ProtocolViolation } => "Beacon's answer broke a protocol rule, so the Desk stopped reading it.",
        _ => "The answer stopped unexpectedly. Try again.",
    };

    public void Dispose()
    {
        Conversation.Changed -= OnConversationChanged;
        _throttle.Dispose();
    }
}
