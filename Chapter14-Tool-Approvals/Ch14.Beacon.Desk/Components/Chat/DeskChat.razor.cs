using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch14.Beacon.Desk.Components.Approvals;
using Ch14.Beacon.Desk.Connection;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Ch14.Beacon.Desk.Components.Chat;

/// <summary>
/// The Desk's chat (Chapter 7's), with approvals. It sends messages and decisions through the circuit's
/// <see cref="AgentConversation"/> and renders whatever view the conversation publishes.
/// </summary>
public sealed partial class DeskChat : IDisposable
{
    private RenderThrottle _throttle = default!;
    private ChatPanel? _panel;
    private ConversationView? _projectedFrom;
    private DeskTranscript _transcript = new([], null, new Dictionary<string, IReadOnlyList<ToolCallView>>());

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    [Inject] private AgentRefusals Refusals { get; set; } = default!;

    [Inject] private ILogger<DeskChat> Logger { get; set; } = default!;

    [Inject] private DispatcherTime Time { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>The shortest time between two renders while a run streams.</summary>
    [Parameter] public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    protected override void OnInitialized()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), RenderInterval);
        Conversation.Changed += OnConversationChanged;
    }

    // Once per circuit, as soon as there is a browser to ask: deadlines show in Dana's own time zone.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && RendererInfo.IsInteractive)
        {
            await Time.DetectAsync(JS);
        }
    }

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
        // Never past an open approval: AG-UI requires it answered or abandoned before the thread moves on.
        var view = Conversation.View;
        if (view.IsBusy || view.OpenInterrupts.Any() || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await Conversation.SendAsync(text);
        await _throttle.FlushAsync();

        if (Conversation.View.Status == ConversationStatus.Stopped && _panel is not null)
        {
            await _panel.FocusComposerAsync();
        }
    }

    private async Task DecideAsync(ApprovalRequest request, ApprovalDecision decision)
    {
        if (Conversation.View.IsBusy)
        {
            return;
        }

        try
        {
            await ApprovalAnswers.SendAsync(Conversation, request, decision);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The conversation refused before sending: the approval expired or a turn is running. An
            // unhandled exception here would end the Blazor Server circuit.
            Logger.LogWarning(ex, "The approval answer for {InterruptId} wasn't sent.", request.Id);
        }

        await _throttle.FlushAsync();

        // Accepted: the dialog is gone, and focus goes where the next question will be typed. Not accepted: the
        // approval is still open (LastAnswerFailure says why), and the dialog takes focus itself.
        if (ApprovalRequest.Current(Conversation.View) is null && _panel is not null)
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

    // From the dialog, once Beacon has refused an answer: a new thread, and focus where Dana types.
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

    // A refused approval answer is explained in the dialog, which stays open; the status bar covers the rest.
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
