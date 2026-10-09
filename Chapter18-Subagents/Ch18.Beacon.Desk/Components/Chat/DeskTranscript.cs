using Beacon.Client.Core;
using Beacon.Desk.Ui;

namespace Ch18.Beacon.Desk.Components.Chat;

/// <summary>
/// What the chat panel shows: the conversation between Dana and the coordinator. The specialists' messages
/// carry a <c>subagentRunId</c> and belong in their lanes on the board, so the chat leaves them out.
/// </summary>
/// <param name="Messages">Finished messages, in order.</param>
/// <param name="Streaming">The coordinator's message still streaming, if any.</param>
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
                    messages.Add(new TranscriptMessage(user.Id, ChatAuthor.User, user.Text));
                    break;

                // The parent agent's own words: no subagentRunId.
                case AssistantMessageView { SubagentRunId: null, Text.Length: > 0 } coordinator:
                    var text = new TranscriptMessage(coordinator.Id, ChatAuthor.Assistant, coordinator.Text) { IsPartial = coordinator.IsPartial };
                    if (coordinator.IsStreaming)
                    {
                        if (streaming is not null)
                        {
                            messages.Add(streaming);
                        }

                        streaming = text;
                    }
                    else
                    {
                        messages.Add(text);
                    }

                    break;
            }
        }

        return new DeskTranscript(messages, streaming);
    }
}
