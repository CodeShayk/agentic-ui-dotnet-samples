using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Beacon.Client.Core;
using Ch11.Beacon.ComponentViews;

namespace Ch11.Beacon.Field;

/// <summary>One row of Beacon Field's transcript.</summary>
public abstract record FieldItem(string Key);

/// <summary>A message: what the technician typed or what Beacon answered.</summary>
public sealed record TextItem(string Key, bool FromUser, string Text) : FieldItem(Key);

/// <summary>
/// A component the agent asked for. The typed accessors exist for compiled bindings: a template binds
/// to <see cref="PartCard"/> rather than casting <see cref="GeneratedComponent.View"/>.
/// </summary>
public sealed record ComponentItem(string Key, GeneratedComponent Component) : FieldItem(Key)
{
    public PartCardView? PartCard => Component.View as PartCardView;

    public ScheduleTimelineView? Timeline => Component.View as ScheduleTimelineView;

    public FaultHistoryChartView? Chart => Component.View as FaultHistoryChartView;

    public string Description => Component.Describe();

    public bool IsDraft => Component.IsDraft;

    /// <summary>The order action, when the server granted it and it hasn't been used.</summary>
    public bool CanOrder => Component.Phase == ComponentPhase.Ready && Component.Actions.Any(a => a.Name == "order" && a.Enabled);

    public string? OrderNote => Component.Actions.FirstOrDefault(a => a.Name == "order") is { Enabled: false, Reason: { } reason } ? reason : null;
}

/// <summary>
/// Beacon Field's chat for Chapter 11: the conversation's view projected into rows, with components
/// projected by the same <see cref="ComponentProjector"/> Beacon Desk uses. Only the templates differ.
/// </summary>
public sealed class FieldChatViewModel : INotifyPropertyChanged
{
    private readonly AgentConversation _conversation;
    private readonly ComponentProjector _projector;
    private readonly HashSet<string> _ordered = [];
    private int _refreshQueued;
    private string _draft = string.Empty;

    public FieldChatViewModel(AgentConversation conversation, ComponentProjector projector)
    {
        _conversation = conversation;
        _projector = projector;
        _conversation.Changed += (_, _) => QueueRefresh();
        SendCommand = new Command<string?>(async text => await SendAsync(text ?? Draft));
        OrderCommand = new Command<ComponentItem>(async item => await OrderAsync(item));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FieldItem> Items { get; } = [];

    public ICommand SendCommand { get; }

    public ICommand OrderCommand { get; }

    public bool IsBusy => _conversation.View.IsBusy;

    public string Draft
    {
        get => _draft;
        set
        {
            _draft = value;
            OnPropertyChanged();
        }
    }

    private async Task SendAsync(string text)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Draft = string.Empty;
        await _conversation.SendAsync(text);
    }

    // The same rule as the Desk: the message comes from the server's accepted props, and only once.
    private async Task OrderAsync(ComponentItem item)
    {
        var request = new ComponentActionRequest(item.Component, "order");
        if (!IsBusy && ComponentActionMessages.Compose(request) is { } text && _ordered.Add(item.Component.CallId))
        {
            await _conversation.SendAsync(text);
        }
    }

    // Changed fires once per event, often on a thread-pool thread. Coalesce bursts (every argument
    // fragment is a change) into one refresh on the UI thread.
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await Task.Delay(50);
                Interlocked.Exchange(ref _refreshQueued, 0);
                Refresh();
            });
        }
    }

    // Replace only the rows that changed, so the list keeps its scroll position and each changed row is
    // templated again: a draft becomes a card, or a fallback, in place.
    private void Refresh()
    {
        var rows = Project(_conversation.View);
        for (var i = 0; i < rows.Count; i++)
        {
            if (i >= Items.Count)
            {
                Items.Add(rows[i]);
            }
            else if (!Equals(Items[i], rows[i]))
            {
                Items[i] = rows[i];
            }
        }

        while (Items.Count > rows.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        OnPropertyChanged(nameof(IsBusy));
    }

    private List<FieldItem> Project(ConversationView view)
    {
        List<FieldItem> rows = [];
        foreach (var message in view.Messages)
        {
            switch (message)
            {
                case UserMessageView user:
                    rows.Add(new TextItem(user.Id, FromUser: true, user.Text));
                    break;
                case AssistantMessageView { Text.Length: > 0 } assistant:
                    rows.Add(new TextItem(assistant.Id, FromUser: false, assistant.Text));
                    break;
            }

            if (message is AssistantMessageView caller)
            {
                foreach (var call in view.ToolCallsOf(caller))
                {
                    if (_projector.Project(call) is { } component)
                    {
                        rows.Add(new ComponentItem(call.Id, _ordered.Contains(call.Id) ? component.WithActionTaken("order") : component));
                    }
                }
            }
        }

        return rows;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
