using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ch15.Beacon.Agent.Input;

/// <summary>
/// What the model sees when a run resumes: its own calls, rebuilt from the server's records, and the
/// answers as their results. The client's resume list never reaches the model or the SDK. Answers are
/// applied here, once, because the gate has accepted each of them exactly once.
/// </summary>
public static class InputExchange
{
    public static IReadOnlyList<ChatMessage> Apply(IReadOnlyList<PendingInput> decided, InputForms forms)
    {
        if (decided.Count == 0)
        {
            return [];
        }

        List<AIContent> calls = [];
        List<AIContent> results = [];
        foreach (var input in decided)
        {
            calls.Add(input.ToCall());
            results.Add(new FunctionResultContent(input.ToolCallId, input is { Status: InputStatus.Answered, Answer: { } answer }
                ? forms.ById(input.Question.FormId).Apply(input, answer)
                : Abandoned()));
        }

        return [new ChatMessage(ChatRole.Assistant, calls), new ChatMessage(ChatRole.Tool, results)];
    }

    private static JsonElement Abandoned() => JsonSerializer.SerializeToElement(
        new InputAbandoned("abandoned", "The technician dismissed the request without answering. Don't ask again unless they bring it up."),
        Ch15JsonContext.Default.InputAbandoned);
}
