using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Microsoft.AspNetCore.Components;

namespace Ch26.Beacon.Desk.Workspace;

/// <summary>
/// Drives the circuit's <see cref="AgentConversation"/>: sends questions and approval answers, and
/// re-renders whenever the conversation publishes a new view.
/// </summary>
public sealed partial class DeskWorkspace : IDisposable
{
    private RenderThrottle _throttle = default!;
    private ChatPanel? _panel;

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    [Inject] private ILogger<DeskWorkspace> Logger { get; set; } = default!;

    /// <summary>The shortest time between two renders while a run streams. Zero renders every change.</summary>
    [Parameter] public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    protected override void OnInitialized()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), RenderInterval);
        Conversation.Changed += OnConversationChanged;
    }

    // Raised on whichever thread changed the view; the throttle marshals the render through InvokeAsync.
    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e) => _ = _throttle.RequestAsync();

    private async Task SendAsync(string text)
    {
        var view = Conversation.View;
        if (view.IsBusy || view.OpenInterrupts.Any() || string.IsNullOrWhiteSpace(text))
        {
            return; // one turn at a time, and never past an open approval
        }

        await Conversation.SendAsync(text);
        await _throttle.FlushAsync();
    }

    private async Task DecideAsync(ApprovalRequest request, bool approved)
    {
        if (Conversation.View.IsBusy)
        {
            return;
        }

        try
        {
            await Conversation.RespondAsync([request.Answer(approved)]);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // An unhandled exception here would end the Blazor Server circuit.
            Logger.LogWarning(ex, "The answer to approval {ApprovalId} wasn't sent.", request.Id);
        }

        await _throttle.FlushAsync();
        if (_panel is not null)
        {
            await _panel.FocusComposerAsync(); // the prompt is gone; focus goes where the next question is typed
        }
    }

    private void NewChat()
    {
        if (!Conversation.View.IsBusy)
        {
            Conversation.Reset();
        }
    }

    public void Dispose()
    {
        Conversation.Changed -= OnConversationChanged;
        _throttle.Dispose();
    }
}
