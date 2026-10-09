using System.Globalization;
using System.Text.Json;
using Beacon.Client.Core;
using Beacon.Desk.Ui;

namespace Ch22.Beacon.Desk.Components.Chat;

/// <summary>One tool call as the transcript shows it, under the message that made it.</summary>
public sealed record ToolLine(string Id, string Text, bool Done);

/// <summary>
/// What the chat panel shows, projected from the view: user and assistant messages in order, each assistant
/// message's tool calls as short lines, and the message still streaming.
/// </summary>
public sealed record DeskTranscript(
    IReadOnlyList<TranscriptMessage> Messages,
    TranscriptMessage? Streaming,
    IReadOnlyDictionary<string, IReadOnlyList<ToolLine>> Tools)
{
    public static DeskTranscript From(ConversationView view)
    {
        List<TranscriptMessage> messages = [];
        TranscriptMessage? streaming = null;
        Dictionary<string, IReadOnlyList<ToolLine>> tools = new(StringComparer.Ordinal);

        foreach (var message in view.Messages)
        {
            switch (message)
            {
                case UserMessageView user:
                    messages.Add(new TranscriptMessage(user.Id, ChatAuthor.User, user.Text));
                    break;

                case AssistantMessageView assistant:
                    var calls = view.ToolCallsOf(assistant).Select(Line).ToList();
                    if (calls.Count > 0)
                    {
                        tools[assistant.Id] = calls;
                    }

                    var entry = new TranscriptMessage(assistant.Id, ChatAuthor.Assistant, assistant.Text) { IsPartial = assistant.IsPartial };
                    if (assistant.IsStreaming)
                    {
                        streaming = entry;
                    }
                    else if (assistant.Text.Length > 0 || calls.Count > 0)
                    {
                        messages.Add(entry);
                    }

                    break;
            }
        }

        return new DeskTranscript(messages, streaming, tools);
    }

    private static ToolLine Line(ToolCallView call)
    {
        var done = call.Status == ToolCallStatus.Completed;
        if (call.Name == "ask_for_line_voltages")
        {
            return new ToolLine(call.Id, Readings(call.Result) ?? "Asked for the line voltages", done);
        }

        var arguments = call.Arguments is { ValueKind: JsonValueKind.Object } args
            ? string.Join(", ", args.EnumerateObject().Select(p => p.Value.ToString()))
            : string.Empty;
        return new ToolLine(call.Id, $"{call.Name}({arguments})", done);
    }

    // The technician's answer, as the agent recorded it: the result of the call the question stood for.
    private static string? Readings(string? result)
    {
        if (result is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        if (root.TryGetProperty("cancelled", out _))
        {
            return "Readings: not taken";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"Readings: L1-L2 {root.GetProperty("l1l2").GetDecimal():0} V · L2-L3 {root.GetProperty("l2l3").GetDecimal():0} V · L1-L3 {root.GetProperty("l1l3").GetDecimal():0} V");
    }
}
