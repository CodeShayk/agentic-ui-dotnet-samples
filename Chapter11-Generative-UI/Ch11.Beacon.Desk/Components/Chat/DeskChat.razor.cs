using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch11.Beacon.ComponentViews;
using Microsoft.AspNetCore.Components;

namespace Ch11.Beacon.Desk.Components.Chat;

/// <summary>
/// The Desk's chat (Chapter 7's, with components). It sends and stops through the circuit's
/// <see cref="AgentConversation"/>, renders whatever view the conversation publishes, and turns a
/// button pressed in a generated component into the next user turn.
/// </summary>
public sealed partial class DeskChat : IDisposable
{
    private readonly HashSet<(string CallId, string Action)> _taken = [];
    private RenderThrottle _throttle = default!;
    private ChatPanel? _panel;
    private ConversationView? _projectedFrom;
    private DeskTranscript _transcript = new([], null, new Dictionary<string, GeneratedComponent>(), new Dictionary<string, IReadOnlyList<ToolCallView>>());

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    [Inject] private ComponentProjector Projector { get; set; } = default!;

    /// <summary>The shortest time between two renders while a run streams.</summary>
    [Parameter] public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    protected override void OnInitialized()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), RenderInterval);
        Conversation.Changed += OnConversationChanged;
    }

    // Raised on whichever thread changed the view; every argument fragment the tap sees is one change. The
    // throttle renders at most every RenderInterval, so a component fills in smoothly without a render per fragment.
    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e) => _ = _throttle.RequestAsync();

    private DeskTranscript Project(ConversationView view)
    {
        if (!ReferenceEquals(view, _projectedFrom))
        {
            (_transcript, _projectedFrom) = (DeskTranscript.From(view, Projector, _taken), view);
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

    // A button in a generated component. The click becomes the next user turn, written from the props the
    // server accepted; the agent decides what to do with it, and its tools enforce their own rules.
    private async Task OnComponentActionAsync(ComponentActionRequest request)
    {
        if (Conversation.View.IsBusy || ComponentActionMessages.Compose(request) is not { } text
            || !_taken.Add((request.Component.CallId, request.ActionName)))
        {
            return; // busy, not allowed, or already pressed
        }

        _projectedFrom = null; // show the button as taken right away
        await SendAsync(text);
    }

    private void NewChat()
    {
        if (!Conversation.View.IsBusy)
        {
            Conversation.Reset(); // a new thread, an empty view
            Projector.Clear();
            _taken.Clear();
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

    public void Dispose()
    {
        Conversation.Changed -= OnConversationChanged;
        _throttle.Dispose();
    }
}
