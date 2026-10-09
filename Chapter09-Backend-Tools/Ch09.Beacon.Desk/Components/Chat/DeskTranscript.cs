using Beacon.Client.Core;
using Beacon.Desk.Ui;

namespace Ch09.Beacon.Desk.Components.Chat;

/// <summary>
/// What the chat panel shows, projected from the conversation's view state: user and assistant
/// messages, and after each assistant message that called tools, one entry for its tool calls, which
/// the panel's message template renders as chips and cards.
/// </summary>
/// <param name="Messages">Finished messages and tool entries, in order.</param>
/// <param name="Streaming">The assistant message still streaming, if any.</param>
public sealed record DeskTranscript(IReadOnlyList<TranscriptMessage> Messages, TranscriptMessage? Streaming)
{
    /// <summary>Tool entries are transcript entries with this prefix on the assistant message's ID.</summary>
    public const string ToolsPrefix = "tools:";

    /// <summary>
    /// What a screen reader hears, once, when a tool entry first appears. The cards themselves aren't
    /// announced; the answer that follows them is.
    /// </summary>
    public const string ToolsAnnouncement = "Looking things up.";

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

            // A message's tool calls follow it, in the order the agent opened them.
            if (message is AssistantMessageView { ToolCallIds.Count: > 0 } caller)
            {
                messages.Add(new TranscriptMessage(ToolsPrefix + caller.Id, ChatAuthor.Assistant, ToolsAnnouncement));
            }
        }

        return new DeskTranscript(messages, streaming);
    }
}
