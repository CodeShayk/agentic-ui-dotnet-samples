using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Beacon.Client.Core;

/// <summary>Settings for an <see cref="AgentConversation"/>.</summary>
public sealed class ConversationOptions
{
    /// <summary>The thread to start on. <see langword="null"/> mints a new one (<c>thread_</c> and a GUID).</summary>
    public string? ThreadId { get; init; }

    /// <summary>
    /// Tools the client runs itself (frontend tools). They go on the shared <see cref="ChatOptions.Tools"/>,
    /// so <c>AGUIChatClient</c> declares them in <c>RunAgentInput.tools</c> and its built-in function
    /// invocation runs them when the agent calls them. Don't wrap <c>AGUIChatClient</c> in another
    /// <c>UseFunctionInvocation()</c>.
    /// </summary>
    public IList<AITool> Tools { get; init; } = [];

    /// <summary>
    /// The tap on the transport under <c>AGUIChatClient</c>, if there is one. With it, the view shows
    /// messages as they start and tool calls while their arguments stream and their tools run.
    /// </summary>
    public WireEventTap? Tap { get; init; }

    /// <summary>The longest a turn may take before it fails with <see cref="FailureKind.TimedOut"/>. The default is two minutes.</summary>
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether every run sends the current shared state as <c>RunAgentInput.state</c>, closing the
    /// loop the protocol describes: state persists across runs and the next run carries it back. On by default.
    /// </summary>
    public bool SendState { get; init; } = true;

    /// <summary>
    /// Called for every run's input, including the extra runs a frontend tool causes within one turn,
    /// after the conversation has set the thread, the state and the turn's <see cref="RunInputs"/>.
    /// Use it for values that should go on every run, such as <c>context</c>.
    /// </summary>
    public Action<RunAgentInput, ConversationView>? ConfigureInput { get; init; }

    /// <summary>Where failures are logged. Messages and state are never logged.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>The clock that decides whether an interrupt has expired (<c>expiresAt</c>). <see langword="null"/> uses the system clock.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>
/// <c>RunAgentInput</c> fields for one turn. They go on the turn's first run only: a frontend tool
/// makes <c>AGUIChatClient</c> start a second run in the same turn, and a resume or a branch point
/// must not be sent twice.
/// </summary>
public sealed record RunInputs
{
    /// <summary>The state to send instead of the conversation's current shared state.</summary>
    public JsonElement? State { get; init; }

    /// <summary>Context entries for the agent (<c>RunAgentInput.context</c>).</summary>
    public IList<AGUIContext>? Context { get; init; }

    /// <summary>Application data passed through to the agent (<c>RunAgentInput.forwardedProps</c>).</summary>
    public JsonElement? ForwardedProperties { get; init; }

    /// <summary>The run this one branches from (<c>RunAgentInput.parentRunId</c>).</summary>
    public string? ParentRunId { get; init; }

    /// <summary>
    /// Resume entries written by hand, for example to abandon an interrupt (status <c>cancelled</c>).
    /// When set, <c>AGUIChatClient</c> uses them instead of translating response content.
    /// </summary>
    public IList<AGUIResume>? Resume { get; init; }

    /// <summary>
    /// AG-UI messages to send instead of the converted history. For protocol features the
    /// <c>ChatMessage</c> conversion can't express; most clients never set it.
    /// </summary>
    public IList<AGUIMessage>? Messages { get; init; }
}

/// <summary>Data for <see cref="AgentConversation.Changed"/>.</summary>
/// <param name="view">The view after the change.</param>
/// <param name="action">The action that caused it.</param>
public sealed class ConversationChangedEventArgs(ConversationView view, ConversationAction action) : EventArgs
{
    /// <summary>The view after the change. A later change may already have replaced it; render <see cref="AgentConversation.View"/>.</summary>
    public ConversationView View { get; } = view;

    /// <summary>The action that caused the change.</summary>
    public ConversationAction Action { get; } = action;
}
