using AGUI.Abstractions;
using Beacon.Client.Core;
using Beacon.Desk.Ui;

namespace Ch13.Beacon.Desk.Progress;

/// <summary>
/// Decides whether a change to the conversation is worth a render of one panel, and when. Three
/// filters, cheapest first: the action (some can't touch this panel's slice at all), the projection
/// (a new view whose slice reads the same as last time renders nothing new) and the clock (a
/// <see cref="RenderThrottle"/> folds a burst of changes into one render per interval).
/// </summary>
/// <typeparam name="T">The panel's projection of the view. It must have value equality.</typeparam>
/// <param name="latest">The conversation's latest view: changes can be raised out of order across threads.</param>
/// <param name="project">The panel's projection.</param>
/// <param name="concerns">False for actions that can't change this panel's slice.</param>
/// <param name="throttle">Paces the renders.</param>
public sealed class SliceRenderGate<T>(
    Func<ConversationView> latest,
    Func<ConversationView, T> project,
    Func<ConversationAction, bool> concerns,
    RenderThrottle throttle)
{
    private readonly Lock _gate = new();
    private T _current = project(latest());

    /// <summary>The projection the panel should show.</summary>
    public T Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>How many changes reached the throttle (each may or may not become its own render).</summary>
    public int Passed { get; private set; }

    /// <summary>Call it from <see cref="AgentConversation.Changed"/>, on any thread.</summary>
    public Task OnChangedAsync(ConversationChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!concerns(e.Action))
        {
            return Task.CompletedTask; // 1. this kind of change never touches the slice
        }

        lock (_gate)
        {
            var next = project(latest());
            if (EqualityComparer<T>.Default.Equals(next, _current))
            {
                return Task.CompletedTask; // 2. a new view, but the slice reads the same
            }

            _current = next;
            Passed++;
        }

        // 3. At the end of a turn, show the final state now; otherwise at most once per interval.
        return e.Action is TurnEnded ? throttle.FlushAsync() : throttle.RequestAsync();
    }
}

/// <summary>Which actions can change which slice, for <see cref="SliceRenderGate{T}"/>.</summary>
public static class SliceConcerns
{
    /// <summary>
    /// Everything except the tap's wire events (text and tool-call bookkeeping) and the text and tool
    /// results that arrive as updates: none of them can change shared state, steps or activity.
    /// </summary>
    public static bool Progress(ConversationAction action) => action is not (WireEventObserved
        or UpdateReceived { Update.RawRepresentation: TextMessageContentEvent or ToolCallResultEvent });

    /// <summary>Every action: the transcript changes with almost all of them.</summary>
    public static bool Any(ConversationAction action) => true;
}
