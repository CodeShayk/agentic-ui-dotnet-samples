using Beacon.Client.Core;
using Beacon.Desk.Ui;

namespace Ch17.Beacon.Desk.Components.Chat;

/// <summary>What the chat panel shows, projected from the conversation's view state, as in Chapter 7.</summary>
/// <param name="Messages">Finished messages, in order.</param>
/// <param name="Streaming">The assistant message still streaming, if any.</param>
public sealed record DeskTranscript(IReadOnlyList<TranscriptMessage> Messages, TranscriptMessage? Streaming)
{
    public static DeskTranscript From(ConversationView view)
    {
        List<TranscriptMessage> messages = [];
        TranscriptMessage? streaming = null;

        foreach (var message in view.Messages)
        {
            switch (message)
            {
                // A message of attachments alone still gets a line; its attachments render in the template.
                case UserMessageView user:
                    messages.Add(new TranscriptMessage(user.Id, ChatAuthor.User, user.Text));
                    break;

                case AssistantMessageView { Text.Length: > 0 } assistant:
                    var shown = new TranscriptMessage(assistant.Id, ChatAuthor.Assistant, assistant.Text) { IsPartial = assistant.IsPartial };
                    if (assistant.IsStreaming)
                    {
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
        }

        return new DeskTranscript(messages, streaming);
    }
}
