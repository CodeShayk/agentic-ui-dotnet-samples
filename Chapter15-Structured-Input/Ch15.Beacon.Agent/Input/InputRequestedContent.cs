using System.Globalization;
using System.Text.Json.Serialization;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace Ch15.Beacon.Agent.Input;

/// <summary>
/// The model asked for something only a person can supply, and the server recorded the request. It travels
/// up the chat pipeline in place of the model's call; the endpoint's <see cref="AGUIStreamOptions.MapInterrupt"/>
/// mapper (<see cref="InputInterrupts"/>) turns it into an interrupt on <c>RUN_FINISHED</c>.
/// </summary>
/// <remarks>
/// <c>AGUI.Server</c> serializes every update it converts and attaches the JSON as <c>rawEvent</c> to the
/// events it produces from it, so this type must be registered with the endpoint's JSON options
/// (<c>AIJsonUtilities.AddAIContentType</c>) and anything it serializes may reach the client. It
/// serializes its ID and nothing else.
/// </remarks>
public sealed class InputRequestedContent : AIContent
{
    /// <summary>The server's ID for the request; it becomes the interrupt's ID.</summary>
    public required string InputId { get; init; }

    /// <summary>The server's record, for the mapper. Never serialized.</summary>
    [JsonIgnore]
    public PendingInput? Request { get; init; }
}

/// <summary>How Beacon's input requests look on the wire.</summary>
public static class InputInterrupts
{
    /// <summary>The stream options for one run: interrupts for Beacon's input requests, everything else as the SDK does it.</summary>
    public static AGUIStreamOptions StreamOptions() =>
        new AGUIStreamOptions().MapInterrupt(content =>
            content is InputRequestedContent { Request: { } request } ? For(request) : null);

    /// <summary>The interrupt for a recorded request: every field comes from the server's record.</summary>
    public static AGUIInterrupt For(PendingInput input) => new()
    {
        Id = input.Id,
        Reason = InterruptReasons.InputRequired,
        Message = input.Question.Message,
        ResponseSchema = input.Question.Schema,
        ExpiresAt = Timestamp(input.ExpiresAt),
        Metadata = input.Question.Metadata,

        // No toolCallId: the model's call never goes on the wire, and a client reads reason "tool_call"
        // plus a toolCallId as an approval. This is a question, not a call waiting for a yes.
    };

    /// <summary>ISO 8601 in UTC, to the second: what <c>expiresAt</c> conventionally carries.</summary>
    public static string Timestamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
