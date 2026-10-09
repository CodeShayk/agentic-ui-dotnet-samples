using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ch02.HandBuilt.Console;

/// <summary>The request the client sends for every run.</summary>
public sealed record RunAgentInput
{
    public required string ThreadId { get; init; }

    public required string RunId { get; init; }

    public required IReadOnlyList<InputMessage> Messages { get; init; }

    public string? ProtocolVersion { get; init; }
}

/// <summary>One message of the conversation history. This client only sends text.</summary>
public sealed record InputMessage(string Id, string Role, string Content);

/// <summary>
/// Any event read off the wire. One flat record is enough for this chapter's six events: each
/// fills the fields it has, and an event type the client doesn't know still parses.
/// </summary>
public sealed record WireEvent
{
    public required string Type { get; init; }

    public string? ThreadId { get; init; }

    public string? RunId { get; init; }

    public string? MessageId { get; init; }

    public string? Delta { get; init; }

    public string? Message { get; init; }

    public string? Code { get; init; }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunAgentInput))]
[JsonSerializable(typeof(WireEvent))]
internal sealed partial class ClientJsonContext : JsonSerializerContext;
