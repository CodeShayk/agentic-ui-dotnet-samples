using AGUI.Abstractions;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch22.Beacon.Desk.Sessions;

/// <summary>
/// Turns stored AG-UI events back into the actions a live turn produces, so a restored conversation goes
/// through the same pure reducer as a live one. That is the whole restore: no second code path for history.
/// </summary>
public static class ConversationReplay
{
    /// <summary>The actions one replayed turn produces: start, one per event, end.</summary>
    public static IEnumerable<ConversationAction> Actions(IEnumerable<BaseEvent> events)
    {
        yield return new TurnStarted();

        foreach (var evt in events)
        {
            // Events AGUIChatClient never surfaces reach the reducer through the tap; the rest as updates.
            yield return evt is TextMessageStartEvent or TextMessageEndEvent or ToolCallStartEvent or ToolCallArgsEvent or ToolCallEndEvent
                ? new WireEventObserved(evt)
                : new UpdateReceived(new ChatResponseUpdate { RawRepresentation = evt });
        }

        yield return new TurnEnded(TurnEnd.Completed);
    }

    /// <summary>The view a set of stored events leaves on a fresh thread, folded through <see cref="ConversationReducer"/>.</summary>
    public static ConversationView Fold(string threadId, IEnumerable<BaseEvent> events) =>
        Actions(events).Aggregate(
            ConversationReducer.Reduce(ConversationView.Empty, new ConversationReset(threadId)),
            ConversationReducer.Reduce);

    /// <summary>Restores into a live conversation: a new view on the thread, then the replayed turn, one action at a time.</summary>
    public static void Restore(AgentConversation conversation, string threadId, IEnumerable<BaseEvent> events)
    {
        conversation.Reset(threadId);
        foreach (var action in Actions(events))
        {
            conversation.Dispatch(action);
        }
    }
}
