using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Microsoft.AspNetCore.Components;

namespace Ch16.Beacon.Desk.Components.Chat;

/// <summary>
/// The Desk's chat. It sends and stops through the circuit's <see cref="AgentConversation"/>, renders whatever
/// view the conversation publishes and remembers which replies' reasoning Dana has opened.
/// </summary>
public sealed partial class DeskChat : IDisposable
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private RenderThrottle _throttle = default!;
    private ChatPanel? _panel;
    private ConversationView? _projectedFrom;
    private ReplyTranscript _transcript = new([], null, new Dictionary<string, Reply>());

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    [Inject] private TimeProvider Clock { get; set; } = default!;

    /// <summary>The shortest time between two renders while a run streams.</summary>
    [Parameter] public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    protected override void OnInitialized()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), RenderInterval, Clock);
        Conversation.Changed += OnConversationChanged;
    }

    // Raised on whichever thread changed the view; the throttle marshals to the circuit and renders the latest.
    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e) => _ = _throttle.RequestAsync();

    private ReplyTranscript Project(ConversationView view)
    {
        if (!ReferenceEquals(view, _projectedFrom))
        {
            (_transcript, _projectedFrom) = (ReplyTranscript.From(view), view);
        }

        return _transcript;
    }

    private async Task SendAsync(string text)
    {
        if (Conversation.View.IsBusy || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await Conversation.SendAsync(text);
        await _throttle.FlushAsync();

        if (_panel is not null && Conversation.View.Status != ConversationStatus.Stopped)
        {
            await _panel.FocusComposerAsync();
        }
    }

    // Open state lives here, keyed by reply, so it survives the reply moving from the streaming slot to the list.
    private void SetExpanded(string replyId, bool open)
    {
        if (open)
        {
            _expanded.Add(replyId);
        }
        else
        {
            _expanded.Remove(replyId);
        }
    }

    private void NewChat()
    {
        if (!Conversation.View.IsBusy)
        {
            _expanded.Clear();
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

    internal static string? ErrorOf(ConversationView view) => view.Failure switch
    {
        null => null,
        { Kind: FailureKind.AgentError or FailureKind.TimedOut } failure => failure.Message,
        { Kind: FailureKind.Unreachable } => "Beacon's agent can't be reached. Check that it's running.",
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
