using System.Text.Json;

namespace Ch02.HandBuilt.Agent.Protocol;

/// <summary>
/// The body of every AG-UI request. Only <see cref="ThreadId"/>, <see cref="RunId"/> and
/// <see cref="Messages"/> are required; members this record doesn't model (state, tools,
/// context, forwardedProps, resume) are ignored on the way in, which is what the spec asks of
/// a producer that doesn't use them.
/// </summary>
public sealed record RunAgentInput
{
    /// <summary>The conversation this run belongs to.</summary>
    public required string ThreadId { get; init; }

    /// <summary>This run. The client mints a new one for every request.</summary>
    public required string RunId { get; init; }

    /// <summary>The whole conversation so far, in order. The server keeps no other copy.</summary>
    public required IReadOnlyList<InputMessage> Messages { get; init; }

    /// <summary>The protocol version the client speaks, such as "1.0".</summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>The run that spawned this one, if any.</summary>
    public string? ParentRunId { get; init; }
}

/// <summary>One message of the conversation history, as the client sends it.</summary>
public sealed record InputMessage
{
    public required string Id { get; init; }

    /// <summary><c>user</c>, <c>assistant</c>, <c>system</c>, <c>developer</c>, <c>tool</c>, <c>activity</c> or <c>reasoning</c>.</summary>
    public required string Role { get; init; }

    /// <summary>
    /// A string for most messages. A user message may instead carry an array of content parts
    /// (Chapter 17), and an activity message carries an object, so the raw JSON is kept.
    /// </summary>
    public JsonElement Content { get; init; }

    /// <summary>The message's text: the string content, or the text parts of a multipart message.</summary>
    public string? GetText() => Content.ValueKind switch
    {
        JsonValueKind.String => Content.GetString(),
        JsonValueKind.Array => string.Join('\n', Content.EnumerateArray()
            .Where(part => part.TryGetProperty("type", out var type) && type.ValueEquals("text"))
            .Select(part => part.TryGetProperty("text", out var text) ? text.GetString() : null)
            .OfType<string>()),
        _ => null,
    };
}
