using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Client.Core;

namespace Ch22.Tests.Infrastructure;

/// <summary>
/// Everything about a view that Beacon Desk renders, as text: the thread and its status, every message with
/// its text and calls, every tool call with its arguments, status and result, the shared state, the open
/// questions with their schemas and the compaction notice. Two views that agree here look the same on screen.
/// </summary>
/// <remarks>
/// It leaves out bookkeeping the Desk doesn't show: which run a message was folded during (a restored branch
/// arrives in one run), who executed a tool call (a snapshot carries messages, not executors), the list of
/// every run the client watched and the request content behind an interrupt, which the client rebuilds.
/// </remarks>
public static class Rendered
{
    public static string Of(ConversationView view)
    {
        var text = new StringBuilder()
            .AppendLine($"thread {view.ThreadId} · {view.Status} · run {view.CurrentRun?.RunId} after {view.CurrentRun?.ParentRunId ?? "(start)"} · {view.CurrentRun?.Outcome}");

        foreach (var message in view.Messages)
        {
            text.AppendLine(message switch
            {
                UserMessageView u => $"user {u.Id}: {u.Text}",
                AssistantMessageView a => $"assistant {a.Id}: {a.Text} [{string.Join(", ", a.ToolCallIds)}]{(a.IsPartial ? " (partial)" : "")}",
                ToolMessageView t => $"tool {t.Id} for {t.ToolCallId}: {t.Content}",
                ReasoningMessageView r => $"reasoning {r.Id}: {r.Text}",
                _ => $"{message.Role} {message.Id}",
            });
        }

        foreach (var call in view.ToolCalls)
        {
            text.AppendLine($"call {call.Id} {call.Name} {call.ArgumentsText} {call.Status} => {call.Result}");
        }

        text.AppendLine($"state {view.State.Document?.ToJsonString() ?? "(none)"}");

        foreach (var interrupt in view.OpenInterrupts)
        {
            text.AppendLine($"open {interrupt.Id} {interrupt.Kind} {interrupt.Message} until {interrupt.ExpiresAt} {interrupt.ResponseSchema?.GetRawText()}");
        }

        var compaction = view.CustomEvents.LastOrDefault(e => e.Name == "beacon.compaction");
        text.AppendLine($"compaction {(compaction?.Value is { } value ? value.GetRawText() : "(none)")}");
        return text.ToString();
    }
}

/// <summary>The chapter's conversation about WO-1005, driven through the Desk's client stack.</summary>
public static class Elena
{
    public const string C33Question = "WO-1005 tripped on C33 again. What should Elena check before we condemn the compressor?";
    public const string NoteQuestion = "Draft a short note for the facilities manager at Northgate.";

    public static readonly JsonElement Readings = JsonSerializer.SerializeToElement(
        new Dictionary<string, decimal> { ["l1l2"] = 462, ["l2l3"] = 458, ["l1l3"] = 431 },
        Ch22JsonContext.Default.DictionaryStringDecimal);

    /// <summary>The question that ends with Beacon asking for readings.</summary>
    public static Task AskAsync(DeskStack desk) => desk.Conversation.SendAsync(C33Question, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>The readings, sent as the answer to the open question.</summary>
    public static Task AnswerAsync(DeskStack desk) =>
        desk.Conversation.RespondAsync(
            [new InterruptResponseContent(desk.View.OpenInterrupts.Single().Id) { Payload = Readings }],
            cancellationToken: TestContext.Current.CancellationToken);

    public static Task AskForNoteAsync(DeskStack desk) => desk.Conversation.SendAsync(NoteQuestion, cancellationToken: TestContext.Current.CancellationToken);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, decimal>))]
internal sealed partial class Ch22JsonContext : System.Text.Json.Serialization.JsonSerializerContext;
