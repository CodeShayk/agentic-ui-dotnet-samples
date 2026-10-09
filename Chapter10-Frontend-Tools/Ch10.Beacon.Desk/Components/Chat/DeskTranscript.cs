using Beacon.Client.Core;
using Beacon.Desk.Ui;

namespace Ch10.Beacon.Desk.Components.Chat;

/// <summary>
/// What the chat panel shows, projected from the conversation's view state. This is the Desk's own
/// decision about the transcript; Beacon Field projects the same view differently.
/// </summary>
/// <param name="Messages">Finished messages and tool-activity lines, in order.</param>
/// <param name="Streaming">The assistant message still streaming, if any.</param>
public sealed record DeskTranscript(IReadOnlyList<TranscriptMessage> Messages, TranscriptMessage? Streaming)
{
    /// <summary>Tool-activity lines are transcript entries with this prefix on the assistant message's ID.</summary>
    public const string ToolLinePrefix = "tools:";

    public static DeskTranscript From(ConversationView view)
    {
        List<TranscriptMessage> messages = [];
        TranscriptMessage? streaming = null;

        foreach (var message in view.Messages)
        {
            switch (message)
            {
                case UserMessageView user:
                    messages.Add(new TranscriptMessage(user.Id, ChatAuthor.User, user.Text));
                    break;

                case AssistantMessageView { Text.Length: > 0 } assistant:
                    var shown = new TranscriptMessage(assistant.Id, ChatAuthor.Assistant, assistant.Text) { IsPartial = assistant.IsPartial };
                    if (assistant.IsStreaming)
                    {
                        // One streaming slot: an earlier message still open moves into the list.
                        if (streaming is not null)
                        {
                            messages.Add(streaming);
                        }

                        streaming = shown;
                    }
                    else
                    {
                        messages.Add(shown);
                    }

                    break;
            }

            // A message's tool calls follow it as one activity line.
            if (message is AssistantMessageView { ToolCallIds.Count: > 0 } caller)
            {
                messages.Add(new TranscriptMessage(ToolLinePrefix + caller.Id, ChatAuthor.Assistant, ToolLine.Describe(view.ToolCallsOf(caller))));
            }

            // Reasoning, activity and tool messages have no place in the Desk's chat.
        }

        return new DeskTranscript(messages, streaming);
    }
}
