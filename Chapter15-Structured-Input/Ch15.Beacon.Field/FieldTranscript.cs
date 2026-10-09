using Beacon.Client.Core;

namespace Ch15.Beacon.Field;

/// <summary>One line in Beacon Field's chat.</summary>
/// <param name="Id">The message or tool call it shows.</param>
/// <param name="Author">"You" or "Beacon".</param>
/// <param name="Text">Plain text. The Field shows model output as text, never as markup.</param>
public sealed record FieldLine(string Id, string Author, string Text);

/// <summary>Beacon Field's projection of the conversation's view state: messages, and a line per tool call.</summary>
public static class FieldTranscript
{
    public static IReadOnlyList<FieldLine> From(ConversationView view)
    {
        List<FieldLine> lines = [];
        foreach (var message in view.Messages)
        {
            switch (message)
            {
                case UserMessageView user:
                    lines.Add(new FieldLine(user.Id, "You", user.Text));
                    break;
                case AssistantMessageView assistant:
                    foreach (var call in view.ToolCallsOf(assistant))
                    {
                        lines.Add(new FieldLine(call.Id, "Beacon", call.IsFinished ? $"Done: {call.Name}." : $"Working: {call.Name}…"));
                    }

                    if (assistant.Text.Length > 0)
                    {
                        lines.Add(new FieldLine(assistant.Id, "Beacon", assistant.IsPartial ? assistant.Text + " (stopped)" : assistant.Text));
                    }

                    break;
            }
        }

        return lines;
    }
}
