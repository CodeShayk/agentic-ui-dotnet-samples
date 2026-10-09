using System.Text.Json.Serialization;

namespace Ch02.HandBuilt.Agent.Protocol;

/// <summary>
/// The events this chapter's endpoint emits. The <c>type</c> discriminator is written first,
/// in AG-UI's SCREAMING_SNAKE_CASE; every other property is camelCase. Chapter 3 covers the
/// rest of the event model.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStartedEvent), "RUN_STARTED")]
[JsonDerivedType(typeof(TextMessageStartEvent), "TEXT_MESSAGE_START")]
[JsonDerivedType(typeof(TextMessageContentEvent), "TEXT_MESSAGE_CONTENT")]
[JsonDerivedType(typeof(TextMessageEndEvent), "TEXT_MESSAGE_END")]
[JsonDerivedType(typeof(RunFinishedEvent), "RUN_FINISHED")]
[JsonDerivedType(typeof(RunErrorEvent), "RUN_ERROR")]
public abstract record AgentEvent;

/// <summary>Opens a run. Carries the IDs from the request and the version this server speaks.</summary>
public sealed record RunStartedEvent(string ThreadId, string RunId) : AgentEvent
{
    public string? ParentRunId { get; init; }

    public string? ProtocolVersion { get; init; }
}

/// <summary>Opens an assistant message. Every later event for it carries the same ID.</summary>
public sealed record TextMessageStartEvent(string MessageId, string Role = "assistant") : AgentEvent;

/// <summary>The next piece of the message's text. Deltas concatenate in arrival order.</summary>
public sealed record TextMessageContentEvent(string MessageId, string Delta) : AgentEvent;

/// <summary>Closes the message.</summary>
public sealed record TextMessageEndEvent(string MessageId) : AgentEvent;

/// <summary>Closes a run that didn't fail. No outcome means success.</summary>
public sealed record RunFinishedEvent(string ThreadId, string RunId) : AgentEvent;

/// <summary>Ends a run that failed. <see cref="Message"/> is for a person to read.</summary>
public sealed record RunErrorEvent(string Message) : AgentEvent
{
    public string? Code { get; init; }
}
