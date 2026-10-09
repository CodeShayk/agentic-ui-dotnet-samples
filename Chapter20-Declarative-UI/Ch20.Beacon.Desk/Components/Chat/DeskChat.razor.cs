using System.Net;
using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch20.Beacon.Desk.Components.Surfaces;
using Ch20.DeclarativeUi;
using Microsoft.AspNetCore.Components;

namespace Ch20.Beacon.Desk.Components.Chat;

/// <summary>
/// The Desk's chat (Chapter 7's, with surfaces). It sends and stops through the circuit's
/// <see cref="AgentConversation"/>, renders whatever view the conversation publishes, and sends a surface's
/// action as the next run's <c>forwardedProps</c>.
/// </summary>
public sealed partial class DeskChat : IDisposable
{
    private RenderThrottle _throttle = default!;
    private ChatPanel? _panel;
    private ConversationView? _projectedFrom;
    private DeskTranscript _transcript = new([], null);
    private string? _formError;

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    [Inject] private SurfaceBoard Board { get; set; } = default!;

    [Inject] private TimeProvider Time { get; set; } = default!;

    /// <summary>The shortest time between two renders while a run streams.</summary>
    [Parameter] public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    protected override void OnInitialized()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), RenderInterval);
        Conversation.Changed += OnConversationChanged;
    }

    // Raised on whichever thread changed the view; the throttle marshals to the circuit and renders at most every RenderInterval.
    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e) => _ = _throttle.RequestAsync();

    private DeskTranscript Project(ConversationView view)
    {
        if (!ReferenceEquals(view, _projectedFrom))
        {
            (_transcript, _projectedFrom) = (DeskTranscript.From(view, Board), view);
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

        _formError = null;
        await Conversation.SendAsync(text);
        await _throttle.FlushAsync();

        if (Conversation.View.Status == ConversationStatus.Stopped && _panel is not null)
        {
            await _panel.FocusComposerAsync(); // Stop just became disabled; don't strand focus on it
        }
    }

    // A surface's button. The action, with its context resolved from the surface's data model, goes to the agent
    // as A2UI's action message in forwardedProps, on a run with no new user message. The server decides what it means.
    private async Task OnSurfaceActionAsync(SurfaceActionRequest request)
    {
        var view = Conversation.View;
        if (view.IsBusy || view.OpenInterrupts.Any() || request.Surface.CreateAction(request.Button, Time.GetUtcNow()) is not { } action)
        {
            return; // busy, or the form isn't open, or its checks don't pass yet
        }

        _formError = null;
        request.Surface.MarkSending();
        await _throttle.FlushAsync();

        await Conversation.ContinueAsync(new RunInputs { ForwardedProperties = action.ToForwardedProperties() });

        if (Conversation.View.Failure is not { } failure)
        {
            request.Surface.MarkSubmitted();
        }
        else
        {
            request.Surface.MarkOpen(); // Beacon refused it or couldn't be reached; the values are still there to fix or resend
            _formError = FormErrorOf(failure);
        }

        await _throttle.FlushAsync();
    }

    private void NewChat()
    {
        if (!Conversation.View.IsBusy)
        {
            Conversation.Reset(); // a new thread, an empty view
            Board.Clear();
            _formError = null;
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
        { Kind: FailureKind.Rejected, StatusCode: { } status } => $"Beacon's agent refused the request (HTTP {(int)status}).",
        { Kind: FailureKind.Unreachable } => "Beacon's agent can't be reached. Check that it's running, then try again.",
        _ => "The answer stopped unexpectedly. Try again.",
    };

    // The agent's action gate answers 400 for values that don't fit and 409 for a form it won't take again.
    internal static string FormErrorOf(RunFailure failure) => failure switch
    {
        { Kind: FailureKind.Rejected, StatusCode: HttpStatusCode.BadRequest } => "Beacon didn't accept these values. Check them and submit again.",
        { Kind: FailureKind.Rejected } => "Beacon won't take this form again: it was already submitted, it has expired or Beacon didn't issue it. Ask for a new one.",
        _ => "Beacon couldn't take the form just now. Your values are still here; submit again when it's back.",
    };

    public void Dispose()
    {
        Conversation.Changed -= OnConversationChanged;
        _throttle.Dispose();
    }
}
