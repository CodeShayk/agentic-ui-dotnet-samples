using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Beacon.Domain;
using Ch12.Beacon.Desk.Sheet;
using Microsoft.AspNetCore.Components;

namespace Ch12.Beacon.Desk.Components.Sheet;

/// <summary>
/// The sheet panel. It owns no copy of the sheet: every render reads the conversation's shared state,
/// and every edit goes through <see cref="SheetEditor"/>, which applies it at once.
/// </summary>
public sealed partial class WorkOrderSheetPanel : IDisposable
{
    private static readonly string[] Statuses = Enum.GetNames<WorkOrderStatus>();

    private RenderThrottle _throttle = default!;
    private string _newItem = "";
    private string _newNote = "";

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    [Inject] private SheetEditor Editor { get; set; } = default!;

    [Inject] private DispatcherBoard Board { get; set; } = default!;

    protected override void OnInitialized()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), TimeSpan.FromMilliseconds(50));
        Conversation.Changed += OnConversationChanged;
    }

    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e) => _ = _throttle.RequestAsync();

    private void Toggle(string itemId, ChangeEventArgs e) => Editor.Apply(new SetItemDone(itemId, e.Value is true));

    private void SetStatus(ChangeEventArgs e)
    {
        if (e.Value is string status && Statuses.Contains(status))
        {
            Editor.Apply(new SetStatus(status));
        }
    }

    private void AddItem()
    {
        if (!string.IsNullOrWhiteSpace(_newItem) && Editor.Apply(new AddItem(Editor.NextItemId(), _newItem.Trim())))
        {
            _newItem = "";
        }
    }

    private void AddNote()
    {
        if (!string.IsNullOrWhiteSpace(_newNote) && Editor.Apply(new AddNote(_newNote.Trim())))
        {
            _newNote = "";
        }
    }

    private void SetBoardDay(ChangeEventArgs e)
    {
        if (DateOnly.TryParse(e.Value as string, System.Globalization.CultureInfo.InvariantCulture, out var day))
        {
            Board.SelectedDate = day; // the next run's context carries it
        }
    }

    private async Task OpenSampleAsync()
    {
        if (!Conversation.View.IsBusy && !Conversation.View.OpenInterrupts.Any())
        {
            await Conversation.SendAsync("Open WO-1001");
        }
    }

    private static string Who(StateChangeSource source) => source switch
    {
        StateChangeSource.Agent => "Beacon",
        StateChangeSource.Client => "you",
        _ => "nobody yet",
    };

    private static string SyncClass(ConversationView view, SheetSync sync) =>
        sync.NeedsResync ? "resync" : view.IsBusy ? "busy" : sync.HasUnsentEdits ? "unsent" : "ok";

    private static string SyncText(ConversationView view, SheetSync sync) => (sync.NeedsResync, view.IsBusy) switch
    {
        (true, true) => "Out of sync: a change from Beacon didn't apply to your copy. Beacon's snapshot at the end of this run resyncs the sheet.",
        (true, false) => "Out of sync: a change from Beacon didn't apply, and no snapshot followed. Your next message sends your version, and Beacon continues from it.",
        (false, true) => "Beacon is working. Your edits apply now and stay on top of Beacon's changes.",
        _ when sync.HasUnsentEdits => "Beacon will see your edits with your next message.",
        _ => "In sync with Beacon.",
    };

    public void Dispose()
    {
        Conversation.Changed -= OnConversationChanged;
        _throttle.Dispose();
    }
}
