using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Beacon.Client.Core;

/// <summary>
/// Something that happened to a conversation. <see cref="ConversationReducer.Reduce"/> folds actions
/// into a <see cref="ConversationView"/>; nothing else changes the view.
/// </summary>
public abstract record ConversationAction;

/// <summary>Starts a new, empty conversation on a thread.</summary>
/// <param name="ThreadId">The AG-UI thread ID to use for every run.</param>
public sealed record ConversationReset(string ThreadId) : ConversationAction;

/// <summary>The user sent a message. It joins the transcript before the run starts.</summary>
/// <param name="Message">The message, with its <see cref="ChatMessage.MessageId"/> set.</param>
public sealed record MessageSent(ChatMessage Message) : ConversationAction;

/// <summary>The client started a turn: one call to the agent, which may hold several runs.</summary>
public sealed record TurnStarted : ConversationAction;

/// <summary>One update from <c>AGUIChatClient.GetStreamingResponseAsync</c>, with or without content.</summary>
/// <param name="Update">The update. Its <see cref="ChatResponseUpdate.RawRepresentation"/> is usually the AG-UI event.</param>
public sealed record UpdateReceived(ChatResponseUpdate Update) : ConversationAction;

/// <summary>
/// An AG-UI event seen on the wire by a <see cref="WireEventTap"/>, before <c>AGUIChatClient</c>
/// converted it. The reducer uses only the events the chat client never surfaces, or surfaces late:
/// <c>TEXT_MESSAGE_START</c>, <c>TEXT_MESSAGE_END</c>, <c>TOOL_CALL_START</c>, <c>TOOL_CALL_ARGS</c>
/// and <c>TOOL_CALL_END</c>. Everything else arrives again as an <see cref="UpdateReceived"/>.
/// </summary>
/// <param name="Event">The event as it came off the transport.</param>
public sealed record WireEventObserved(BaseEvent Event) : ConversationAction;

/// <summary>The turn's stream ended: normally, because the user stopped it or because it failed.</summary>
/// <param name="Reason">How the turn ended.</param>
public sealed record TurnEnded(TurnEnd Reason) : ConversationAction
{
    /// <summary>The failure, when <see cref="Reason"/> is <see cref="TurnEnd.Failed"/>.</summary>
    public RunFailure? Failure { get; init; }

    /// <summary>
    /// Interrupts this turn answered or abandoned whose resuming run never started: the agent refused the
    /// request, or it failed or was stopped first. They stay open, and the failure is recorded on each.
    /// </summary>
    public IReadOnlyList<string> UnacceptedAnswers { get; init; } = [];
}

/// <summary>How a turn's stream ended.</summary>
public enum TurnEnd
{
    /// <summary>The stream ended on its own.</summary>
    Completed,

    /// <summary>The user stopped it.</summary>
    Stopped,

    /// <summary>It failed on the client's side: unreachable, timed out or broken.</summary>
    Failed,
}

/// <summary>The client changed the shared state itself (for example, the dispatcher edited a field).</summary>
/// <param name="Document">The new document. The reducer stores it as given; pass a copy you won't mutate.</param>
public sealed record StateEdited(JsonNode? Document) : ConversationAction;

/// <summary>
/// The agent accepted the client's answers to interrupts: the run carrying them started. Until then an
/// answered interrupt stays open, so an answer the agent refuses can be given again or abandoned.
/// </summary>
/// <param name="InterruptIds">The IDs of the interrupts answered or abandoned.</param>
public sealed record InterruptsAnswered(IReadOnlyList<string> InterruptIds) : ConversationAction;

/// <summary>
/// The client built a run's input and is sending it. The run's <c>STATE_DELTA</c>s apply to the state
/// it carries (or to the empty object when it carries none), so the reducer adopts it as the baseline
/// when it differs from the view's state.
/// </summary>
/// <param name="Input">The input. The reducer reads it and never changes it.</param>
public sealed record RunInputSent(RunAgentInput Input) : ConversationAction;
