using System.Text.Json;
using Beacon.Client.Core;
using Beacon.Desk.Ui;

namespace Ch16.Beacon.Desk.Components.Chat;

/// <summary>What a step in "How I got here" is.</summary>
public enum ReplyStepKind
{
    /// <summary>Reasoning the agent streamed.</summary>
    Thought,

    /// <summary>A tool the agent called on the way.</summary>
    Lookup,
}

/// <summary>One step of a reply's "How I got here".</summary>
/// <param name="Id">The reasoning message or tool call ID.</param>
/// <param name="Kind">Reasoning or a tool call.</param>
/// <param name="Text">The reasoning text as the agent's policy released it, or a description of the call. Untrusted.</param>
public sealed record ReplyStep(string Id, ReplyStepKind Kind, string Text)
{
    /// <summary>True while the reasoning streams or the tool runs.</summary>
    public bool InProgress { get; init; }

    /// <summary>True when the provider sealed its full reasoning with this step. The value itself never reaches the UI.</summary>
    public bool IsSealed { get; init; }
}

/// <summary>Everything one run said: the steps that led to the answer, and the answer.</summary>
/// <param name="Id">The transcript entry's ID (<c>reply:</c> and the run ID).</param>
/// <param name="Steps">Reasoning and tool calls, in the order they arrived.</param>
/// <param name="Answer">The answer text. Untrusted model output, rendered through <c>MarkdownView</c>.</param>
public sealed record Reply(string Id, IReadOnlyList<ReplyStep> Steps, string Answer)
{
    /// <summary>True while the run is in progress.</summary>
    public bool IsStreaming { get; init; }

    /// <summary>True when the run was stopped or failed before the answer was finished.</summary>
    public bool IsPartial { get; init; }
}

/// <summary>
/// What the chat panel shows, projected from the view state: one entry per user message and one per run.
/// A run's reasoning and tool calls become the steps of its reply, and the entry's text is the answer alone,
/// which is what the transcript's live region announces. Reasoning is never read out with the answer.
/// </summary>
public sealed record ReplyTranscript(
    IReadOnlyList<TranscriptMessage> Messages,
    TranscriptMessage? Streaming,
    IReadOnlyDictionary<string, Reply> Replies)
{
    public const string ReplyPrefix = "reply:";

    public static ReplyTranscript From(ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        List<TranscriptMessage> messages = [];
        TranscriptMessage? streaming = null;
        Dictionary<string, Reply> replies = new(StringComparer.Ordinal);
        var liveRun = view.IsBusy ? view.CurrentRun?.RunId : null;
        Builder? current = null;

        foreach (var message in view.Messages)
        {
            switch (message)
            {
                case UserMessageView user:
                    Close();
                    messages.Add(new TranscriptMessage(user.Id, ChatAuthor.User, user.Text));
                    break;

                case ReasoningMessageView reasoning:
                    For(reasoning.RunId ?? reasoning.Id).Steps.Add(new ReplyStep(reasoning.Id, ReplyStepKind.Thought, reasoning.Text)
                    {
                        InProgress = reasoning.IsStreaming,
                        IsSealed = reasoning.EncryptedValue is not null,
                    });
                    break;

                case AssistantMessageView assistant:
                    var reply = For(assistant.RunId ?? assistant.Id);
                    if (assistant.Text.Length > 0)
                    {
                        reply.Answers.Add(assistant.Text);
                    }

                    reply.IsPartial |= assistant.IsPartial;
                    foreach (var call in view.ToolCallsOf(assistant))
                    {
                        reply.Steps.Add(new ReplyStep(call.Id, ReplyStepKind.Lookup, Describe(call))
                        {
                            InProgress = call.Status is ToolCallStatus.Streaming or ToolCallStatus.InProgress,
                        });
                    }

                    break;

                // Tool results, activity and system messages have no place in the Desk's chat.
            }
        }

        Close();
        return new ReplyTranscript(messages, streaming, replies);

        Builder For(string key)
        {
            if (current?.Key != key)
            {
                Close();
                current = new Builder(key);
            }

            return current;
        }

        void Close()
        {
            if (current is not { } done || (done.Steps.Count == 0 && done.Answers.Count == 0))
            {
                current = null;
                return;
            }

            var reply = new Reply(ReplyPrefix + done.Key, [.. done.Steps], string.Join("\n\n", done.Answers))
            {
                IsStreaming = done.Key == liveRun,
                IsPartial = done.IsPartial,
            };

            replies[reply.Id] = reply;
            var entry = new TranscriptMessage(reply.Id, ChatAuthor.Assistant, reply.Answer.Length > 0 || reply.IsStreaming
                ? reply.Answer
                : "Beacon didn't finish an answer.") { IsPartial = reply.IsPartial };

            if (reply.IsStreaming)
            {
                streaming = entry;
            }
            else
            {
                messages.Add(entry);
            }

            current = null;
        }
    }

    // A tool call as a dispatcher would say it.
    private static string Describe(ToolCallView call) => call.Name switch
    {
        "get_fault_history" => $"Read the fault history for {Argument(call, "equipmentId")}",
        "get_work_order" => $"Looked up {Argument(call, "workOrderId")}",
        "check_warranty" => $"Checked the warranty on {Argument(call, "equipmentId")}",
        "search_parts" => $"Searched parts for \"{Argument(call, "query")}\"",
        "get_schedule" => "Checked today's schedule",
        "list_work_orders" => "Listed the work orders",
        _ => $"Called {call.Name}",
    };

    private static string Argument(ToolCallView call, string name) =>
        call.Arguments is { ValueKind: JsonValueKind.Object } arguments
        && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "the unit";

    private sealed class Builder(string key)
    {
        public string Key { get; } = key;

        public List<ReplyStep> Steps { get; } = [];

        public List<string> Answers { get; } = [];

        public bool IsPartial { get; set; }
    }
}
