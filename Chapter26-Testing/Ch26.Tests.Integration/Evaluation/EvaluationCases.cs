using AGUI.Client;
using Ch26.Tests.Integration.Infrastructure;
using Microsoft.Extensions.AI;

namespace Ch26.Tests.Integration.Evaluation;

/// <summary>A question Beacon should answer well, the call a good answer makes and what the answer must rest on.</summary>
/// <param name="Name">The scenario name in reports.</param>
/// <param name="Question">What the dispatcher asks.</param>
/// <param name="Expected">The tool call a good answer makes.</param>
/// <param name="Recording">The recorded run that stands in for the model offline.</param>
/// <param name="ExpectedToPass">Whether the recorded run should pass the deterministic evaluators.</param>
public sealed record EvaluationCase(string Name, string Question, ExpectedToolCall Expected, string Recording, bool ExpectedToPass = true)
{
    public override string ToString() => Name;
}

public static class EvaluationCases
{
    private static readonly ExpectedToolCall StatusCall = new("get_work_order", new Dictionary<string, string> { ["workOrderId"] = "WO-1001" });
    private static readonly ExpectedToolCall WarrantyCall = new("check_warranty", new Dictionary<string, string> { ["equipmentId"] = "EQ-501" });

    public static IReadOnlyList<EvaluationCase> All { get; } =
    [
        new("status-scripted", "What is the status of WO-1001?", StatusCall, Recordings.Status),
        new("status-llama", "What is the status of WO-1001?", StatusCall, Recordings.LlamaStatus),

        // llama3.2 once answered this by listing open work orders and calling WO-1001 "Open". The
        // stream was well formed and the answer fluent; only the evaluator notices.
        new("status-llama-wrong-tool", "What is the status of WO-1001?", StatusCall, Recordings.LlamaStatusWrongTool, ExpectedToPass: false),

        new("warranty-llama", "Is EQ-501 still under warranty?", WarrantyCall, Recordings.LlamaWarranty1),
    ];

    public static TheoryData<string> Names => [.. All.Select(c => c.Name)];

    public static EvaluationCase Named(string name) => All.Single(c => c.Name == name);

    /// <summary>Asks the agent through AGUIChatClient and returns the response as the client received it.</summary>
    public static async Task<ChatResponse> AskAsync(AgentHost agent, string question, CancellationToken cancellationToken)
    {
        var http = agent.CreateClient();
        using var client = new AGUIChatClient(new AGUIChatClientOptions(http, new Uri(http.BaseAddress!, "/agent").ToString()));

        // Streaming, then aggregated: the evaluators need the calls, the results and the final text.
        List<ChatResponseUpdate> updates = [];
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, question) { MessageId = "user-1" }], cancellationToken: cancellationToken))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    /// <summary>What a grounded answer may rely on: the tool results in the response.</summary>
    public static string GroundingOf(ChatResponse response) => string.Join(
        Environment.NewLine,
        response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString()));
}
