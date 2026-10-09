using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Beacon.Client.Core;

/// <summary>
/// One message in the transcript, in the shape AG-UI gives it. The subtypes mirror the protocol's
/// roles, so a renderer can switch on the type: <see cref="UserMessageView"/>,
/// <see cref="AssistantMessageView"/>, <see cref="ToolMessageView"/>, <see cref="ReasoningMessageView"/>,
/// <see cref="ActivityMessageView"/> and <see cref="SystemMessageView"/>.
/// </summary>
/// <param name="Id">The AG-UI message ID, unique within the thread.</param>
public abstract record MessageView(string Id)
{
    /// <summary>The AG-UI role name (<c>user</c>, <c>assistant</c>, <c>tool</c>, <c>reasoning</c>, ...).</summary>
    public abstract string Role { get; }

    /// <summary>The run that produced the message; <see langword="null"/> for messages the client wrote.</summary>
    public string? RunId { get; init; }

    /// <summary>The subagent invocation the message is attributed to; <see langword="null"/> for the parent agent.</summary>
    public string? SubagentRunId { get; init; }

    /// <summary>An opaque value the producer asked the client to keep and return (<c>REASONING_ENCRYPTED_VALUE</c>).</summary>
    public string? EncryptedValue { get; init; }
}

/// <summary>A message from the user.</summary>
/// <param name="Id">The message ID the client chose.</param>
/// <param name="Text">The message's text.</param>
public sealed record UserMessageView(string Id, string Text) : MessageView(Id)
{
    /// <inheritdoc />
    public override string Role => AGUIRoles.User;

    /// <summary>The user's display name, if one was sent.</summary>
    public string? Name { get; init; }

    /// <summary>Non-text parts the user sent (images, audio, documents), as <c>Microsoft.Extensions.AI</c> content.</summary>
    public ImmutableList<AIContent> Attachments { get; init; } = [];
}

/// <summary>A message from the agent: streamed text and the tool calls it carries.</summary>
/// <param name="Id">The AG-UI message ID.</param>
public sealed record AssistantMessageView(string Id) : MessageView(Id)
{
    /// <inheritdoc />
    public override string Role => AGUIRoles.Assistant;

    /// <summary>The text so far. Untrusted model output: render it through a sanitizer, never as raw markup.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The speaker's name, from <c>TEXT_MESSAGE_START</c>, when the producer sent one.</summary>
    public string? Name { get; init; }

    /// <summary>True while text is still arriving.</summary>
    public bool IsStreaming { get; init; }

    /// <summary>True when the run was stopped or failed before the message was finished.</summary>
    public bool IsPartial { get; init; }

    /// <summary>The IDs of the tool calls attached to this message (<c>parentMessageId</c>), in order.</summary>
    public ImmutableList<string> ToolCallIds { get; init; } = [];
}

/// <summary>A tool's result, as the protocol's <c>tool</c> message (<c>TOOL_CALL_RESULT</c>).</summary>
/// <param name="Id">The tool message's ID.</param>
/// <param name="ToolCallId">The call it answers.</param>
/// <param name="Content">The result, usually JSON text. Untrusted.</param>
public sealed record ToolMessageView(string Id, string ToolCallId, string Content) : MessageView(Id)
{
    /// <inheritdoc />
    public override string Role => AGUIRoles.Tool;

    /// <summary>The error the tool reported, when it failed.</summary>
    public string? Error { get; init; }
}

/// <summary>Visible reasoning the model streamed before or between its answers (<c>REASONING_MESSAGE_*</c>).</summary>
/// <param name="Id">The reasoning message's ID.</param>
public sealed record ReasoningMessageView(string Id) : MessageView(Id)
{
    /// <inheritdoc />
    public override string Role => AGUIRoles.Reasoning;

    /// <summary>The reasoning text so far.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>True while reasoning text is still arriving.</summary>
    public bool IsStreaming { get; init; }
}

/// <summary>
/// Structured progress the agent keeps in the message sequence (<c>ACTIVITY_SNAPSHOT</c> and
/// <c>ACTIVITY_DELTA</c>). It is rendering material only and never goes back to the agent.
/// </summary>
/// <param name="Id">The activity message's ID.</param>
/// <param name="ActivityType">The producer's own type name, such as <c>parts-search</c>.</param>
/// <param name="Content">The activity's current content. Read-only.</param>
public sealed record ActivityMessageView(string Id, string ActivityType, JsonObject Content) : MessageView(Id)
{
    /// <inheritdoc />
    public override string Role => AGUIRoles.Activity;
}

/// <summary>A <c>system</c> or <c>developer</c> message the agent restated. The client never sends these back.</summary>
/// <param name="Id">The message ID.</param>
/// <param name="Text">The message text.</param>
/// <param name="IsDeveloper">True for the <c>developer</c> role, false for <c>system</c>.</param>
public sealed record SystemMessageView(string Id, string Text, bool IsDeveloper) : MessageView(Id)
{
    /// <inheritdoc />
    public override string Role => IsDeveloper ? AGUIRoles.Developer : AGUIRoles.System;
}
