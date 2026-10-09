using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch18.Beacon.Desk.Activity;
using Microsoft.AspNetCore.Components;

namespace Ch18.Beacon.Desk.Components.Chat;

/// <summary>
/// The Desk's chat (Chapter 7's) with the coordinator. It sends messages through the circuit's
/// <see cref="AgentConversation"/> and renders whatever view the conversation publishes.
/// </summary>
public sealed partial class DeskChat : IDisposable
{
    private RenderThrottle _throttle = default!;
    private ChatPanel? _panel;
    private ConversationView? _projectedFrom;
    private DeskTranscript _transcript = new([], null);

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    [Inject] private ApprovalFocus Focus { get; set; } = default!;

    /// <summary>The shortest time between two renders while a run streams.</summary>
    [Parameter] public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    protected override void OnInitialized()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), RenderInterval);
        Conversation.Changed += OnConversationChanged;
        Focus.SetComposer(() => _panel?.FocusComposerAsync() ?? ValueTask.CompletedTask);
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

    // The approval waits on the board, not in the chat: this is the keyboard path to it.
    private async Task GoToApprovalAsync(InterruptView interrupt) => await Focus.GoToAsync(interrupt.Id);

    private static string GoToLabel(ConversationView view, InterruptView interrupt) =>
        view.Subagents.FirstOrDefault(s => s.SubagentRunId == interrupt.SubagentRunId) is { Name.Length: > 0 } subagent
            ? $"Go to the {SpecialistBoard.LabelFor(subagent.Name).ToLowerInvariant()}'s approval"
            : "Go to the approval";

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
        { Kind: FailureKind.Rejected, StatusCode: { } status } =>
            $"Beacon's agent refused the request (HTTP {(int)status}), so nothing ran. If another Desk answered this approval first, start a new chat.",
        { Kind: FailureKind.Unreachable } => "Beacon's agent can't be reached. Check that it's running, then start a new chat.",
        { Kind: FailureKind.ProtocolViolation } => "Beacon's answer broke a protocol rule, so the Desk stopped reading it.",
        _ => "The answer stopped unexpectedly. Try again.",
    };

    public void Dispose()
    {
        Conversation.Changed -= OnConversationChanged;
        Focus.SetComposer(null);
        _throttle.Dispose();
    }
}
