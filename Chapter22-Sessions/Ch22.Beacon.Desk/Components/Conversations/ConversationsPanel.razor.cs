using System.Text.Json;
using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch22.Beacon.Desk.Sessions;
using Microsoft.AspNetCore.Components;

namespace Ch22.Beacon.Desk.Components.Conversations;

/// <summary>Lists the stored conversations for a work order and opens one, or one of its branches.</summary>
public sealed partial class ConversationsPanel : IDisposable
{
    private static readonly string[] WorkOrders = ["WO-1001", "WO-1002", "WO-1003", "WO-1004", "WO-1005", "WO-1006"];

    private RenderThrottle _throttle = default!;
    private IReadOnlyList<ThreadSummary> _threads = [];
    private string _workOrderId = "WO-1005";
    private string? _error;
    private ConversationStatus _lastStatus;

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    [Inject] private ConversationSessions Sessions { get; set; } = default!;

    [Inject] private TimeProvider Clock { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), TimeSpan.FromMilliseconds(100), Clock);
        Conversation.Changed += OnConversationChanged;
        Sessions.DocumentChanged += OnDocumentChanged;
        await RefreshAsync();
    }

    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e)
    {
        // When a turn ends the agent has recorded a run: the list may have a new conversation or a new branch.
        var ended = _lastStatus is ConversationStatus.Starting or ConversationStatus.Running && !e.View.IsBusy;
        _lastStatus = e.View.Status;
        _ = ended ? InvokeAsync(ReloadAndRenderAsync) : _throttle.RequestAsync();
    }

    private void OnDocumentChanged() => _ = _throttle.RequestAsync();

    private async Task ChangeWorkOrderAsync(ChangeEventArgs e)
    {
        _workOrderId = e.Value?.ToString() ?? _workOrderId;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            _threads = await Sessions.ListAsync(_workOrderId);
            _error = null;
        }
        catch (HttpRequestException)
        {
            _error = "Beacon's agent can't be reached, so its conversations can't be listed.";
        }
    }

    // Called from a Changed handler, outside Blazor's event dispatch, so it renders itself.
    private async Task ReloadAndRenderAsync()
    {
        await RefreshAsync();
        StateHasChanged();
    }

    private async Task OpenAsync(string threadId, string? runId)
    {
        if (Conversation.View.IsBusy)
        {
            return;
        }

        try
        {
            await Sessions.OpenAsync(threadId, runId);
            _error = null;
        }
        catch (HttpRequestException)
        {
            _error = "Beacon's agent can't be reached, so the conversation can't be opened.";
        }

        await _throttle.FlushAsync();
    }

    /// <summary>The compaction notice from the view: a live one arrives on the run that compacted, a restored one with the branch.</summary>
    private static CompactionInfo? CompactionOf(ConversationView view) =>
        view.CustomEvents.LastOrDefault(e => e.Name == "beacon.compaction")?.Value is { } value
            ? value.Deserialize(ThreadApiJsonContext.Default.CompactionInfo)
            : null;

    public void Dispose()
    {
        Conversation.Changed -= OnConversationChanged;
        Sessions.DocumentChanged -= OnDocumentChanged;
        _throttle.Dispose();
    }
}
