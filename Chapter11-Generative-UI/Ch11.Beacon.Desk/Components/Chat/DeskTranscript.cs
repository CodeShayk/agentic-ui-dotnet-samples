using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch11.Beacon.ComponentViews;

namespace Ch11.Beacon.Desk.Components.Chat;

/// <summary>
/// What the chat panel shows, projected from the conversation's view state: Chapter 7's transcript,
/// with every component call turned into a component entry and the remaining tool calls into activity lines.
/// </summary>
/// <param name="Messages">Finished entries, in order.</param>
/// <param name="Streaming">The one entry still arriving: streaming text, or a component whose props are a preview.</param>
/// <param name="Components">The generated components, by entry ID.</param>
/// <param name="ToolLines">The non-component tool calls of each activity line, by entry ID.</param>
public sealed record DeskTranscript(
    IReadOnlyList<TranscriptMessage> Messages,
    TranscriptMessage? Streaming,
    IReadOnlyDictionary<string, GeneratedComponent> Components,
    IReadOnlyDictionary<string, IReadOnlyList<ToolCallView>> ToolLines)
{
    public const string ToolLinePrefix = "tools:";
    public const string ComponentPrefix = "component:";

    /// <param name="view">The conversation.</param>
    /// <param name="projector">Turns tool calls into the components this Desk knows.</param>
    /// <param name="taken">Actions the user already took, as (call ID, action name), so a button can't be pressed twice.</param>
    public static DeskTranscript From(ConversationView view, ComponentProjector projector, IReadOnlySet<(string CallId, string Action)> taken)
    {
        List<TranscriptMessage> messages = [];
        TranscriptMessage? streaming = null;
        Dictionary<string, GeneratedComponent> components = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<ToolCallView>> toolLines = new(StringComparer.Ordinal);

        // One live slot, as in Chapter 7: when a newer entry starts arriving, the older one joins the list.
        void Add(TranscriptMessage entry, bool live)
        {
            if (!live)
            {
                messages.Add(entry);
                return;
            }

            if (streaming is not null)
            {
                messages.Add(streaming);
            }

            streaming = entry;
        }

        foreach (var message in view.Messages)
        {
            switch (message)
            {
                case UserMessageView user:
                    Add(new TranscriptMessage(user.Id, ChatAuthor.User, user.Text), live: false);
                    break;

                case AssistantMessageView { Text.Length: > 0 } assistant:
                    Add(new TranscriptMessage(assistant.Id, ChatAuthor.Assistant, assistant.Text) { IsPartial = assistant.IsPartial }, assistant.IsStreaming);
                    break;
            }

            if (message is not AssistantMessageView { ToolCallIds.Count: > 0 } caller)
            {
                continue;
            }

            // Ordinary calls share one activity line; each component call gets an entry of its own. Its
            // text is the sentence a screen reader hears once the component is settled.
            List<ToolCallView> activity = [];
            List<GeneratedComponent> generated = [];
            foreach (var call in view.ToolCallsOf(caller))
            {
                if (projector.Project(call) is { } component)
                {
                    foreach (var action in component.Actions.Where(a => taken.Contains((call.Id, a.Name))))
                    {
                        component = component.WithActionTaken(action.Name);
                    }

                    generated.Add(component);
                }
                else
                {
                    activity.Add(call);
                }
            }

            if (activity.Count > 0)
            {
                toolLines[ToolLinePrefix + caller.Id] = activity;
                Add(new TranscriptMessage(ToolLinePrefix + caller.Id, ChatAuthor.Assistant, ToolLine.Describe(activity)), live: false);
            }

            foreach (var component in generated)
            {
                var id = ComponentPrefix + component.CallId;
                components[id] = component;
                Add(new TranscriptMessage(id, ChatAuthor.Assistant, component.Describe()) { IsPartial = component.Phase == ComponentPhase.Abandoned }, component.IsDraft);
            }
        }

        return new DeskTranscript(messages, streaming, components, toolLines);
    }
}
