using System.Globalization;
using System.Text.Json;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch26.Beacon.Desk.Workspace;

/// <summary>One line of an approval request: a label and the value the agent proposed.</summary>
public sealed record ApprovalFact(string Label, string Value);

/// <summary>
/// An open approval interrupt as the prompt shows it, projected from the conversation's Interrupts and
/// ToolCalls slices. The facts are the call's arguments as the agent announced them.
/// </summary>
public sealed record ApprovalRequest(string Id, string Title, string? Message, IReadOnlyList<ApprovalFact> Facts)
{
    /// <summary>The request to send back with the answer, when the client surfaced one.</summary>
    public InputRequestContent? Request { get; init; }

    /// <summary>The call being approved, as the agent announced it.</summary>
    public ToolCallView? Call { get; init; }

    /// <summary>
    /// The answer for <see cref="AgentConversation.RespondAsync"/>. An approval that arrived only on
    /// <c>RUN_FINISHED</c> has no request content, so the answer is built from the announced call.
    /// </summary>
    public ToolApprovalResponseContent Answer(bool approved) => Request switch
    {
        ToolApprovalRequestContent asked => asked.CreateResponse(approved),
        _ when Call is { } call => new ToolApprovalResponseContent(Id, approved, new FunctionCallContent(
            call.Id,
            call.Name,
            call.Arguments is { ValueKind: JsonValueKind.Object } arguments
                ? arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                : new Dictionary<string, object?>())),
        _ => throw new InvalidOperationException($"Approval {Id} names no tool call the client has seen."),
    };

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["workOrderId"] = "Work order",
        ["sku"] = "Part",
        ["quantity"] = "Quantity",
        ["amount"] = "Amount",
        ["description"] = "Description",
    };

    /// <summary>The first open approval in <paramref name="view"/>, or <see langword="null"/> when nothing waits.</summary>
    public static ApprovalRequest? Current(ConversationView view)
    {
        if (view.OpenInterrupts.FirstOrDefault(i => i.Kind == InterruptKind.ToolApproval) is not { } interrupt)
        {
            return null;
        }

        var call = interrupt.ToolCallId is { } id ? view.FindToolCall(id) : null;
        List<ApprovalFact> facts = [];
        if (call?.Arguments is { ValueKind: JsonValueKind.Object } arguments)
        {
            foreach (var argument in arguments.EnumerateObject())
            {
                facts.Add(new ApprovalFact(Labels.GetValueOrDefault(argument.Name, argument.Name), Display(argument)));
            }
        }

        var title = call?.Name switch
        {
            "place_part_order" => "Approve this part order?",
            "charge_customer" => "Approve this charge?",
            _ => "Approve this action?",
        };

        return new ApprovalRequest(interrupt.Id, title, interrupt.Message, facts) { Request = interrupt.Request, Call = call };
    }

    private static string Display(JsonProperty argument) => argument.Value.ValueKind switch
    {
        JsonValueKind.String => argument.Value.GetString() ?? string.Empty,
        JsonValueKind.Number when argument.Name == "amount" && argument.Value.TryGetDecimal(out var amount)
            => amount.ToString("C2", CultureInfo.GetCultureInfo("en-US")),
        _ => argument.Value.GetRawText(),
    };
}
