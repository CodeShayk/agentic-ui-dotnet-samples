using System.Text.Json;
using Beacon.Client.Core;
using Ch10.Field.Tools;

namespace Ch10.Beacon.Field;

/// <summary>One line in Beacon Field's chat.</summary>
/// <param name="Id">The message or tool call it shows.</param>
/// <param name="Author">"You", "Beacon" or "On this phone" for a tool the app ran.</param>
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
                    if (assistant.Text.Length > 0)
                    {
                        lines.Add(new FieldLine(assistant.Id, "Beacon", assistant.IsPartial ? assistant.Text + " (stopped)" : assistant.Text));
                    }

                    foreach (var call in view.ToolCallsOf(assistant))
                    {
                        lines.Add(new FieldLine(call.Id, call.Executor == ToolExecutor.Client ? "On this phone" : "Beacon", Describe(call)));
                    }

                    break;
            }
        }

        return lines;
    }

    private static string Describe(ToolCallView call)
    {
        var running = call.Status is ToolCallStatus.Streaming or ToolCallStatus.InProgress
                      || (call.Status == ToolCallStatus.AwaitingClient && call.Executor == ToolExecutor.Client);
        var status = Status(call.Result);

        return call.Name switch
        {
            FieldTools.GetLocationName when running => "Beacon asked for your location…",
            FieldTools.GetLocationName => status == "located" ? "Shared your location." : "Location not shared.",
            FieldTools.TakePhotoName when running => "Beacon asked for a photo…",
            FieldTools.TakePhotoName => status == "captured" ? "Photo attached." : "No photo taken.",
            FieldTools.ScanPartLabelName when running => "Beacon asked you to read a part label…",
            FieldTools.ScanPartLabelName => status == "read" ? "Part label read." : "No part label read.",
            _ when running => $"Working: {call.Name}…",
            _ => call.IsFinished ? $"Done: {call.Name}." : $"Stopped: {call.Name}.",
        };
    }

    private static string? Status(string? result)
    {
        try
        {
            using var document = JsonDocument.Parse(result ?? "");
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("status", out var status)
                   && status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
