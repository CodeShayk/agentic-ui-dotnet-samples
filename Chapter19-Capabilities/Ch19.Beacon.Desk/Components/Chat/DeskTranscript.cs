using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Microsoft.Extensions.AI;

namespace Ch19.Beacon.Desk.Components.Chat;

/// <summary>What the chat shows, projected from the conversation's view: user text (with its attachments named) and assistant text.</summary>
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
                case UserMessageView user:
                    var attached = user.Attachments.OfType<DataContent>().Select(d => d.Name ?? d.MediaType).ToList();
                    var text = attached.Count == 0 ? user.Text : $"{user.Text}\n\n(attached: {string.Join(", ", attached)})";
                    messages.Add(new TranscriptMessage(user.Id, ChatAuthor.User, text));
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
