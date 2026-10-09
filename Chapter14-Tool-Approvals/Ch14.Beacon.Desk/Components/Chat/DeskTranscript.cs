using Beacon.Client.Core;
using Beacon.Desk.Ui;

namespace Ch14.Beacon.Desk.Components.Chat;

/// <summary>
/// What the chat panel shows, projected from the conversation's view state (Chapter 7's projection).
/// </summary>
/// <param name="Messages">Finished messages and tool-activity lines, in order.</param>
/// <param name="Streaming">The assistant message still streaming, if any.</param>
/// <param name="ToolLines">The calls each tool-activity line shows, by the line's ID.</param>
public sealed record DeskTranscript(
    IReadOnlyList<TranscriptMessage> Messages,
    TranscriptMessage? Streaming,
    IReadOnlyDictionary<string, IReadOnlyList<ToolCallView>> ToolLines)
{
    /// <summary>Tool-activity lines are transcript entries with this prefix on the assistant message's ID.</summary>
    public const string ToolLinePrefix = "tools:";

    public static DeskTranscript From(ConversationView view)
    {
        List<TranscriptMessage> messages = [];
        TranscriptMessage? streaming = null;
        Dictionary<string, IReadOnlyList<ToolCallView>> toolLines = new(StringComparer.Ordinal);

        // A call is shown once, under the message that first carried it. A resumed approval restates the
        // call with a new parentMessageId; the reducer keeps it on its first message, and this guard keeps
        // the transcript right even for a client that didn't.
        HashSet<string> shown = new(StringComparer.Ordinal);

        foreach (var message in view.Messages)
        {
            switch (message)
            {
                case UserMessageView user:
                    messages.Add(new TranscriptMessage(user.Id, ChatAuthor.User, user.Text));
                    break;

                case AssistantMessageView { Text.Length: > 0 } assistant:
                    var text = new TranscriptMessage(assistant.Id, ChatAuthor.Assistant, assistant.Text) { IsPartial = assistant.IsPartial };
                    if (assistant.IsStreaming)
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

            if (message is AssistantMessageView caller)
            {
                var calls = view.ToolCallsOf(caller).Where(c => shown.Add(c.Id)).ToList();
                if (calls.Count > 0)
                {
                    messages.Add(new TranscriptMessage(ToolLinePrefix + caller.Id, ChatAuthor.Assistant, ToolLine.Describe(calls)));
                    toolLines[ToolLinePrefix + caller.Id] = calls;
                }
            }
        }

        return new DeskTranscript(messages, streaming, toolLines);
    }
}
