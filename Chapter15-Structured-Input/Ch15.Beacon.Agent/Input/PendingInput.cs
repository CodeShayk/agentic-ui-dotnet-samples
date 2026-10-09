using System.Text.Json;
using Ch15.Forms;
using Microsoft.Extensions.AI;

namespace Ch15.Beacon.Agent.Input;

/// <summary>Where an input request is in its life. Only an <see cref="Open"/> request can be answered, and only once.</summary>
public enum InputStatus
{
    /// <summary>The model asked in a run that hasn't finished yet.</summary>
    Proposed,

    /// <summary>The run ended with this request's interrupt. Someone can answer it until it expires.</summary>
    Open,

    /// <summary>Answered with a payload that matched the schema.</summary>
    Answered,

    /// <summary>Abandoned: a resume entry with the cancelled status.</summary>
    Cancelled,

    /// <summary>Proposed but never shown to anyone, because the run failed or was stopped.</summary>
    Withdrawn,
}

/// <summary>
/// What the server asks for: the form, the prompt, the schema the answer must match and the metadata
/// that travels with the interrupt. Each <see cref="InputForm"/> builds its own from the server's records.
/// </summary>
/// <param name="FormId">Which <see cref="InputForm"/> asked, so its answer goes back to it.</param>
/// <param name="EquipmentId">The equipment the question is about, checked against the store when it was asked.</param>
/// <param name="Message">The prompt for whoever answers, written by the server.</param>
/// <param name="Schema">The <c>responseSchema</c> as it goes on the wire.</param>
/// <param name="Form">The same schema, parsed. The server checks answers against this, never against a schema a client sends.</param>
/// <param name="Metadata">The interrupt's <c>metadata</c>: context for the person, not part of the answer.</param>
public sealed record InputQuestion(string FormId, string EquipmentId, string Message, JsonElement Schema, FormSchema Form, JsonElement Metadata);

/// <summary>
/// One request for input the server raised, bound to the thread, the run and the model's call that asked
/// for it. The ID is the server's own and unguessable; it is the interrupt's ID on the wire. Records are
/// immutable: the store replaces one version with the next.
/// </summary>
public sealed record PendingInput
{
    public required string Id { get; init; }

    public required string ThreadId { get; init; }

    /// <summary>The run that raised the request.</summary>
    public required string RunId { get; init; }

    /// <summary>The model's call that asked; the answer goes back to the model as its result.</summary>
    public required string ToolCallId { get; init; }

    public required string ToolName { get; init; }

    /// <summary>The call's arguments, exactly as the model sent them.</summary>
    public required JsonElement Arguments { get; init; }

    public required InputQuestion Question { get; init; }

    public required DateTimeOffset ProposedAt { get; init; }

    /// <summary>After this, the request can only be abandoned. Whole seconds, so the wire and the record agree.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public InputStatus Status { get; init; } = InputStatus.Proposed;

    /// <summary>The validated payload, once answered.</summary>
    public JsonElement? Answer { get; init; }

    /// <summary>The resume entry's metadata, as the client sent it. Recorded, never acted on.</summary>
    public JsonElement? AnswerMetadata { get; init; }

    /// <summary>The resuming run that carried the answer or the abandonment.</summary>
    public string? DecidedInRun { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>The model's call, rebuilt from the record, for the exchange the model sees on resume.</summary>
    public FunctionCallContent ToCall() =>
        new(ToolCallId, ToolName, Arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal));
}

/// <summary>A decision about one open request, as the resume gate accepted it.</summary>
/// <param name="InputId">The request.</param>
/// <param name="Outcome"><see cref="InputStatus.Answered"/> or <see cref="InputStatus.Cancelled"/>.</param>
/// <param name="RunId">The resuming run.</param>
/// <param name="At">When the server accepted it.</param>
/// <param name="Answer">The validated payload, for an answer.</param>
/// <param name="Metadata">The resume entry's metadata, if it was an object.</param>
public sealed record InputDecision(string InputId, InputStatus Outcome, string RunId, DateTimeOffset At, JsonElement? Answer = null, JsonElement? Metadata = null);
