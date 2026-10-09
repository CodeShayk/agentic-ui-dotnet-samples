using System.Globalization;
using System.Net;
using System.Text.Json;
using Beacon.Client.Core;
using Ch14.Beacon.Desk.Connection;
using Microsoft.Extensions.AI;

namespace Ch14.Beacon.Desk.Components.Approvals;

/// <summary>The label this Desk puts on every request to the agent (the <c>X-Beacon-Client</c> header).</summary>
public sealed record DeskClient(string Id)
{
    public const string HeaderName = "X-Beacon-Client";
}

/// <summary>What kind of value an editable argument holds, from its JSON Schema <c>type</c>.</summary>
public enum EditableKind
{
    Integer,
    Number,
    Text,
}

/// <summary>One argument a dispatcher may change, with the constraints the server's schema gives it.</summary>
public sealed record EditableField(string Name, string Label, EditableKind Kind, string Value)
{
    public decimal? Minimum { get; init; }

    public decimal? Maximum { get; init; }

    public bool ExclusiveMinimum { get; init; }

    public int? MaxLength { get; init; }
}

/// <summary>
/// An approval interrupt as the dialog shows it, projected from the conversation's Interrupts and
/// ToolCalls slices. Everything here is the server's: its message, its price, its expiry and its
/// schema, which says which arguments may change (those not pinned with <c>const</c>).
/// </summary>
public sealed record ApprovalRequest
{
    public required string Id { get; init; }

    public required string ToolCallId { get; init; }

    public required string ToolName { get; init; }

    public required string Title { get; init; }

    public required string Message { get; init; }

    /// <summary>The call's arguments as first announced: the proposal the dispatcher is deciding on.</summary>
    public required JsonElement Arguments { get; init; }

    public decimal? Amount { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public IReadOnlyList<EditableField> EditableFields { get; init; } = [];

    public int MaxReasonLength { get; init; } = 500;

    /// <summary>The request content <c>AGUIChatClient</c> surfaced, which a plain approval answers.</summary>
    public InputRequestContent? Request { get; init; }

    /// <summary>Why the last answer to it didn't reach a run, if one didn't. The approval is still open.</summary>
    public RunFailure? LastFailure { get; init; }

    /// <summary>The agent's reason for refusing the last answer (its problem <c>title</c>), when it gave one.</summary>
    public string? LastRefusalTitle { get; init; }

    /// <summary>The title of the agent's 409 for a repeat of an answer whose call already ran.</summary>
    public const string AlreadyAnsweredTitle = "Interrupt already answered";

    /// <summary>
    /// The agent already carried out this approval in an earlier run, whose end the Desk never saw. Answering
    /// again can't succeed, and asking Beacon again from scratch would act twice.
    /// </summary>
    public bool AlreadyCarriedOut =>
        LastFailure is { Kind: FailureKind.Rejected, StatusCode: HttpStatusCode.Conflict } && LastRefusalTitle == AlreadyAnsweredTitle;

    public bool CanEdit => EditableFields.Count > 0;

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiresAt && expiresAt <= now;

    /// <summary>The open approval the dialog should show, if any. One at a time, oldest first.</summary>
    /// <param name="view">The conversation's view.</param>
    /// <param name="latestRefusal">The circuit's latest refusal (<see cref="AgentRefusals.Latest"/>), for the reason behind a 4xx.</param>
    public static ApprovalRequest? Current(ConversationView view, AgentRefusedException? latestRefusal = null) =>
        view.OpenInterrupts.FirstOrDefault(i => i.Kind == InterruptKind.ToolApproval) is { } interrupt ? From(interrupt, view, latestRefusal) : null;

    public static ApprovalRequest From(InterruptView interrupt, ConversationView view, AgentRefusedException? latestRefusal = null)
    {
        var call = interrupt.ToolCallId is { } callId ? view.FindToolCall(callId) : null;
        var proposed = (interrupt.Request as ToolApprovalRequestContent)?.ToolCall as FunctionCallContent;
        var toolName = call?.Name ?? proposed?.Name ?? "tool";

        // ToolCallView.Arguments is parsed once, when the call first closes. Its ArgumentsText isn't used:
        // a resumed run restates the whole call and the reducer appends the restatement to that text.
        var arguments = call?.Arguments ?? JsonSerializer.SerializeToElement(proposed?.Arguments ?? new Dictionary<string, object?>(),
            AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, object?>)));

        var schema = interrupt.ResponseSchema;
        return new ApprovalRequest
        {
            Id = interrupt.Id,
            ToolCallId = interrupt.ToolCallId ?? string.Empty,
            ToolName = toolName,
            Title = toolName switch
            {
                "place_part_order" => "Approve a part order",
                "charge_customer" => "Approve a customer charge",
                _ => $"Approve {toolName}",
            },
            Message = interrupt.Message ?? $"Beacon wants to run {toolName}.",
            Arguments = arguments,
            Amount = Number(interrupt.Metadata, "amount"),
            ExpiresAt = DateTimeOffset.TryParse(interrupt.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt) ? expiresAt : null,
            EditableFields = Editable(schema, arguments),
            MaxReasonLength = Path(schema, "properties", "reason", "maxLength") is { ValueKind: JsonValueKind.Number } max && max.TryGetInt32(out var length) ? length : 500,
            Request = interrupt.Request,
            LastFailure = interrupt.LastAnswerFailure,

            // The conversation keeps only a refusal's status; the reason is the latest refusal's, if the statuses agree.
            LastRefusalTitle = interrupt.LastAnswerFailure is { Kind: FailureKind.Rejected, StatusCode: { } status }
                && latestRefusal?.StatusCode == status ? latestRefusal.Title : null,
        };
    }

    // editedArgs is a full replacement of the arguments; the ones pinned with "const" can't change.
    private static List<EditableField> Editable(JsonElement? schema, JsonElement arguments)
    {
        List<EditableField> fields = [];
        if (Path(schema, "properties", "editedArgs", "properties") is not { ValueKind: JsonValueKind.Object } properties)
        {
            return fields;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object || property.Value.TryGetProperty("const", out _))
            {
                continue;
            }

            var kind = property.Value.TryGetProperty("type", out var type) ? type.GetString() : null;
            var current = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property.Name, out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText()
                : string.Empty;

            fields.Add(new EditableField(property.Name, LabelFor(property.Name), kind switch
            {
                "integer" => EditableKind.Integer,
                "number" => EditableKind.Number,
                _ => EditableKind.Text,
            }, current)
            {
                Minimum = Number(property.Value, "minimum") ?? Number(property.Value, "exclusiveMinimum"),
                ExclusiveMinimum = property.Value.TryGetProperty("exclusiveMinimum", out _),
                Maximum = Number(property.Value, "maximum"),
                MaxLength = Path(property.Value, "maxLength") is { ValueKind: JsonValueKind.Number } max && max.TryGetInt32(out var length) ? length : null,
            });
        }

        return fields;
    }

    private static string LabelFor(string name) => name switch
    {
        "quantity" => "Quantity",
        "amount" => "Amount (USD)",
        "description" => "Invoice description",
        _ => name,
    };

    private static decimal? Number(JsonElement? element, string name) =>
        Path(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDecimal(out var number) ? number : null;

    private static JsonElement? Path(JsonElement? element, params string[] names)
    {
        var current = element;
        foreach (var name in names)
        {
            if (current is not { ValueKind: JsonValueKind.Object } node || !node.TryGetProperty(name, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}
