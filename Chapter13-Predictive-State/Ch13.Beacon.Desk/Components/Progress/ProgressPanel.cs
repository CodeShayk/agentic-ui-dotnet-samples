using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch13.Beacon.Desk.Progress;
using Microsoft.AspNetCore.Components;

namespace Ch13.Beacon.Desk.Components.Progress;

/// <summary>
/// A panel that shows one projection of the conversation and renders only when that projection
/// changes, at most once per <see cref="RenderInterval"/>. The plan, the report and the status line
/// each derive from it, so a report delta never re-renders the plan and a text delta re-renders neither.
/// </summary>
/// <typeparam name="T">The panel's projection, with value equality.</typeparam>
public abstract class ProgressPanel<T> : ComponentBase, IDisposable
{
    private RenderThrottle _throttle = default!;
    private int _renders;

    [Inject] protected AgentConversation Conversation { get; set; } = default!;

    /// <summary>The shortest time between two renders while a run streams.</summary>
    [Parameter] public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Decides when a change is worth rendering, and holds what the panel shows.</summary>
    protected SliceRenderGate<T> Gate { get; private set; } = default!;

    /// <summary>The panel's projection of a view.</summary>
    protected abstract T Project(ConversationView view);

    /// <summary>False for actions that can't change this panel's projection.</summary>
    protected virtual bool Concerns(ConversationAction action) => SliceConcerns.Progress(action);

    /// <summary>Counts renders, for the <c>data-renders</c> attribute the proof reads.</summary>
    protected int CountRender() => ++_renders;

    protected override void OnInitialized()
    {
        // The throttle's trailing render fires on a timer thread, so it marshals through InvokeAsync.
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), RenderInterval);
        Gate = new SliceRenderGate<T>(() => Conversation.View, Project, Concerns, _throttle);
        Conversation.Changed += OnConversationChanged;
    }

    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e) => _ = Gate.OnChangedAsync(e);

    public void Dispose()
    {
        Conversation.Changed -= OnConversationChanged;
        _throttle.Dispose();
        GC.SuppressFinalize(this);
    }
}
