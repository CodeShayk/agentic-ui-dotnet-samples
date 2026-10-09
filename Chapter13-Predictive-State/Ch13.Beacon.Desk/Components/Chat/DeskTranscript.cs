using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch13.Beacon.Desk.Progress;

namespace Ch13.Beacon.Desk.Components.Chat;

/// <summary>
/// What the chat panel shows, projected from the conversation's view: user and assistant text, and the
/// agent's activity messages in the place they arrived. Equality is by value, so the chat can skip a
/// render when a change left its transcript as it was.
/// </summary>
/// <param name="Messages">Finished messages and activity cards, in order.</param>
/// <param name="Streaming">The assistant message still streaming, if any.</param>
public sealed record DeskTranscript(IReadOnlyList<TranscriptMessage> Messages, TranscriptMessage? Streaming)
{
    /// <summary>Activity cards are transcript entries with this prefix on the activity message's ID.</summary>
    public const string ActivityPrefix = "activity:";

    public bool Equals(DeskTranscript? other) =>
        other is not null && Equals(Streaming, other.Streaming) && Messages.SequenceEqual(other.Messages);

    public override int GetHashCode() => HashCode.Combine(Messages.Count, Streaming);

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

                // The text is what screen readers hear when the card first appears; the card renders the rest.
                case ActivityMessageView activity when ChecksView.From(activity) is { } checks:
                    messages.Add(new TranscriptMessage(ActivityPrefix + activity.Id, ChatAuthor.Assistant, checks.Describe()));
                    break;
            }
        }

        return new DeskTranscript(messages, streaming);
    }
}
