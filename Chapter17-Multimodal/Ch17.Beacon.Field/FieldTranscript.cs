using Beacon.Client.Core;
using Ch17.Media;
using Microsoft.Extensions.AI;

namespace Ch17.Beacon.Field;

/// <summary>One line in Beacon Field's chat.</summary>
/// <param name="Id">The message it shows.</param>
/// <param name="Author">"You" or "Beacon".</param>
/// <param name="Text">Plain text. The Field shows model output as text, never as markup.</param>
/// <param name="Attachments">The message's attachments, and any Beacon set aside, as one line each.</param>
public sealed record FieldLine(string Id, string Author, string Text, string Attachments)
{
    public bool HasAttachments => Attachments.Length > 0;
}

/// <summary>An attachment waiting in the tray.</summary>
public sealed record TrayLine(DataContent Item, string Name, string Detail);

/// <summary>Beacon Field's projection of the conversation's view state.</summary>
public static class FieldTranscript
{
    public static IReadOnlyList<FieldLine> From(ConversationView view)
    {
        var refused = view.CustomEvents
            .Where(e => e.Name == MediaRefusalReader.EventName)
            .SelectMany(e => MediaRefusalReader.Read(e.Value))
            .ToDictionary(p => (p.MessageId, p.Part), p => p.Reason);

        List<FieldLine> lines = [];
        foreach (var message in view.Messages)
        {
            switch (message)
            {
                case UserMessageView user:
                    lines.Add(new FieldLine(user.Id, "You", user.Text, Describe(user, refused)));
                    break;
                case AssistantMessageView { Text.Length: > 0 } assistant:
                    lines.Add(new FieldLine(assistant.Id, "Beacon", assistant.IsPartial ? assistant.Text + " (stopped)" : assistant.Text, string.Empty));
                    break;
            }
        }

        return lines;
    }

    // The wire part index is the attachment's position after the text part, as Beacon.Client.Core sends it.
    private static string Describe(UserMessageView user, Dictionary<(string, int), string> refused) =>
        string.Join('\n', user.Attachments.Select((attachment, i) =>
        {
            var line = attachment is DataContent data
                ? $"Attached: {data.Name ?? "attachment"} ({ByteSize.Format(data.Data.Length)})"
                : "Attached: a link";
            return refused.TryGetValue((user.Id, (user.Text.Length > 0 ? 1 : 0) + i), out var reason)
                ? $"{line}. Not sent to Beacon: {reason}."
                : line;
        }));
}
