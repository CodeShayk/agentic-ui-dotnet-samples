using System.Collections.Immutable;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Beacon.Client.Core;

/// <summary>
/// Folds what happens in a conversation into a <see cref="ConversationView"/>. <see cref="Reduce"/>
/// is a pure function: the same view and action always give the same result, it never mutates its
/// input and it does no I/O, so it can be tested by replaying recorded streams through it.
/// </summary>
/// <remarks>
/// The reducer is split by slice across partial files: messages, tool calls, state and activity,
/// reasoning, steps and subagents, interrupts and <c>MESSAGES_SNAPSHOT</c> reconciliation.
/// </remarks>
public static partial class ConversationReducer
{
    /// <summary>The key under which <c>AGUIChatClient</c> stores subagent attribution on updates and messages.</summary>
    public const string SubagentRunIdKey = "agui.subagentRunId";

    private const int Retained = 50;

    /// <summary>Returns the view that results from applying <paramref name="action"/> to <paramref name="view"/>.</summary>
    /// <param name="view">The current view. It is not modified.</param>
    /// <param name="action">What happened.</param>
    /// <returns>The new view, or <paramref name="view"/> itself when nothing changed.</returns>
    public static ConversationView Reduce(ConversationView view, ConversationAction action)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(action);

        return action switch
        {
            ConversationReset reset => ConversationView.Empty with { ThreadId = reset.ThreadId },
            MessageSent sent => AddSentMessage(view, sent.Message),
            TurnStarted => view with { Status = ConversationStatus.Starting, Failure = null },
            UpdateReceived or WireEventObserved when IsAbandoned(view) => view, // the run failed; ignore the rest
            UpdateReceived received => ApplyUpdate(view, received.Update),
            WireEventObserved observed when IsWireOnly(observed.Event) => ApplyEvent(view, observed.Event, attributedTo: null),
            TurnEnded ended => EndTurn(view, ended),
            StateEdited edited => EditState(view, edited.Document),
            InterruptsAnswered answered => MarkAnswered(view, answered.InterruptIds),
            RunInputSent sent => AdoptSentState(view, sent.Input),
            _ => view,
        };
    }

    // AGUIChatClient yields no update for these events (or, for TOOL_CALL_END, buffers it until the
    // call's result arrives), so a WireEventTap is the only way to see them as they happen. Every
    // other event reaches the reducer as an update; applying it from the tap too would apply it twice.
    private static bool IsWireOnly(BaseEvent evt) =>
        evt is TextMessageStartEvent or TextMessageEndEvent or ToolCallStartEvent or ToolCallArgsEvent or ToolCallEndEvent;

    // One update can carry two things: the AG-UI event it came from (RawRepresentation) and the
    // Microsoft.Extensions.AI content the client made of it. The event goes first; then only the
    // content the event doesn't already describe.
    private static ConversationView ApplyUpdate(ConversationView view, ChatResponseUpdate update)
    {
        string? attributedTo = null;
        update.AdditionalProperties?.TryGetValue(SubagentRunIdKey, out attributedTo);

        var evt = update.RawRepresentation as BaseEvent;
        if (evt is not null)
        {
            view = ApplyEvent(view, evt, attributedTo, update.Role);
        }

        foreach (var content in update.Contents)
        {
            view = content switch
            {
                TextContent text when evt is not TextMessageContentEvent =>
                    AppendText(view, update.MessageId ?? update.ResponseId ?? "assistant", text.Text, RoleName(update.Role), attributedTo),
                TextReasoningContent reasoning when evt is not (ReasoningMessageContentEvent or ReasoningEncryptedValueEvent) =>
                    ApplyReasoningContent(view, reasoning, update, attributedTo),
                FunctionCallContent call => UpsertCall(view, call, update.MessageId, attributedTo),
                FunctionResultContent result when evt is not ToolCallResultEvent =>
                    CompleteToolCall(view, result.CallId, result.CallId, ResultText(result.Result), result.Exception?.Message, attributedTo),
                ToolApprovalRequestContent approval => AddApprovalRequest(view, approval, update.MessageId, attributedTo),
                InterruptRequestContent interrupt => AddInterruptRequest(view, interrupt, attributedTo),
                ErrorContent error when evt is not RunErrorEvent => Warn(view, $"The client reported an error: {error.Message}"),
                _ => view, // UsageContent repeats the terminal event's usage; anything else has no slice yet.
            };
        }

        return view;
    }

    // The event switch: one line per AG-UI event type, routed to the slice it changes.
    private static ConversationView ApplyEvent(ConversationView view, BaseEvent evt, string? attributedTo, ChatRole? role = null)
    {
        view = CloseChunkStreams(view, evt, attributedTo);

        return evt switch
        {
            RunStartedEvent e => StartRun(view, e),
            RunFinishedEvent e => FinishRun(view, e),
            RunErrorEvent e => FailRun(view, e),
            StepStartedEvent e => StartStep(view, e.StepName, e.SubagentRunId ?? attributedTo),
            StepFinishedEvent e => FinishStep(view, e.StepName, e.SubagentRunId ?? attributedTo),

            TextMessageStartEvent e => OpenMessage(view, e.MessageId, e.Role, e.Name, e.SubagentRunId ?? attributedTo),
            TextMessageContentEvent e => AppendText(view, e.MessageId, e.Delta, RoleName(role), e.SubagentRunId ?? attributedTo),
            TextMessageEndEvent e => CloseMessage(view, e.MessageId),
            TextMessageChunkEvent e => ApplyTextChunk(view, e, e.SubagentRunId ?? attributedTo),

            ToolCallStartEvent e => StartToolCall(view, e.ToolCallId, e.ToolCallName, e.ParentMessageId, e.SubagentRunId ?? attributedTo),
            ToolCallArgsEvent e => AppendToolArguments(view, e.ToolCallId, e.Delta),
            ToolCallEndEvent e => EndToolCall(view, e.ToolCallId),
            ToolCallChunkEvent e => ApplyToolCallChunk(view, e, e.SubagentRunId ?? attributedTo),
            ToolCallResultEvent e => CompleteToolCall(view, e.ToolCallId, e.MessageId, e.Content.ToString(), error: null, e.SubagentRunId ?? attributedTo),

            StateSnapshotEvent e => ReplaceState(view, e.Snapshot),
            StateDeltaEvent e => PatchState(view, e.Delta),
            MessagesSnapshotEvent e => ReconcileMessages(view, e.Messages ?? []),
            ActivitySnapshotEvent e => ApplyActivitySnapshot(view, e, e.SubagentRunId ?? attributedTo),
            ActivityDeltaEvent e => ApplyActivityDelta(view, e),

            ReasoningStartEvent e => view with { OpenReasoningSpans = view.OpenReasoningSpans.Add(e.MessageId) },
            ReasoningEndEvent e => view with { OpenReasoningSpans = view.OpenReasoningSpans.Remove(e.MessageId) },
            ReasoningMessageStartEvent e => OpenReasoning(view, e.MessageId, e.SubagentRunId ?? attributedTo),
            ReasoningMessageContentEvent e => AppendReasoning(view, e.MessageId, e.Delta, e.SubagentRunId ?? attributedTo),
            ReasoningMessageEndEvent e => CloseReasoning(view, e.MessageId),
            ReasoningMessageChunkEvent e => ApplyReasoningChunk(view, e, e.SubagentRunId ?? attributedTo),
            ReasoningEncryptedValueEvent e => AttachEncryptedValue(view, e),

            SubagentStartedEvent e => StartSubagent(view, e),
            SubagentFinishedEvent e => FinishSubagent(view, e),
            SubagentErrorEvent e => FailSubagent(view, e),

            CustomEvent e => view with { CustomEvents = Keep(view.CustomEvents.Add(e)) },
            RawEvent e => view with { RawEvents = Keep(view.RawEvents.Add(e)) },
            _ => view,
        };
    }

    // ---- The run lifecycle ------------------------------------------------------------------

    private static ConversationView StartRun(ConversationView view, RunStartedEvent e)
    {
        if (view.Runs.Exists(r => r.RunId == e.RunId))
        {
            return view; // already applied
        }

        var run = new RunView(e.RunId)
        {
            ThreadId = e.ThreadId,
            ParentRunId = e.ParentRunId,
            ProtocolVersion = e.ProtocolVersion,
        };

        view = view with { Runs = view.Runs.Add(run), Status = ConversationStatus.Running, ThreadId = view.ThreadId ?? e.ThreadId };

        return view.ThreadId == e.ThreadId
            ? view
            : Warn(view, $"Run '{e.RunId}' reported thread '{e.ThreadId}', but this conversation is on '{view.ThreadId}'.");
    }

    private static ConversationView FinishRun(ConversationView view, RunFinishedEvent e)
    {
        var index = view.Runs.FindLastIndex(r => r.RunId == e.RunId);
        if (index < 0)
        {
            view = StartRun(view, new RunStartedEvent { ThreadId = e.ThreadId, RunId = e.RunId });
            index = view.Runs.Count - 1;
        }

        // Applied more than once when usage is present (AGUIChatClient attaches the event to each
        // usage update as well), so everything here is idempotent.
        var run = view.Runs[index] with
        {
            Outcome = e.Outcome switch
            {
                RunFinishedInterruptOutcome => RunOutcome.Interrupted,
                RunFinishedCancelledOutcome => RunOutcome.Cancelled,
                _ => RunOutcome.Succeeded, // success, absent, or an outcome this client doesn't know
            },
            Result = e.Result,
            Usage = e.Usage is { Count: > 0 } usage ? [.. usage] : [],
            PendingToolCallIds = e.Outcome is RunFinishedSuccessOutcome { PendingToolCallIds: { Count: > 0 } pending } ? [.. pending] : [],
        };

        view = view with { Runs = view.Runs.SetItem(index, run) };

        if (e.Outcome is RunFinishedInterruptOutcome { Interrupts: { } interrupts })
        {
            foreach (var interrupt in interrupts)
            {
                view = UpsertInterrupt(view, FromInterrupt(interrupt, run.RunId));
            }
        }

        return CloseOpenItems(view, run.RunId, failed: false);
    }

    private static ConversationView FailRun(ConversationView view, RunErrorEvent e)
    {
        view = view with { Failure = new RunFailure(FailureKind.AgentError, e.Message) { Code = e.Code } };

        // A run can fail before it starts (an unreachable model), so there may be no run to close.
        if (view.CurrentRun is not { Outcome: RunOutcome.Running } run)
        {
            return view;
        }

        run = run with { Outcome = RunOutcome.Failed, Usage = e.Usage is { Count: > 0 } usage ? [.. usage] : run.Usage };
        view = view with { Runs = view.Runs.SetItem(view.Runs.Count - 1, run) };
        return CloseOpenItems(view, run.RunId, failed: true);
    }

    // The client's side of the lifecycle: the stream ended, the user stopped it or it broke. Answers whose
    // resuming run never started stay open, with the reason recorded on each.
    private static ConversationView EndTurn(ConversationView view, TurnEnded ended)
    {
        view = CloseTurn(view, ended);
        return ended.UnacceptedAnswers.Count == 0 || view.Failure is not { } failure
            ? view
            : RecordUnaccepted(view, ended.UnacceptedAnswers, failure);
    }

    private static ConversationView CloseTurn(ConversationView view, TurnEnded ended)
    {
        var run = view.CurrentRun;
        var running = run is { Outcome: RunOutcome.Running };

        switch (ended.Reason)
        {
            case TurnEnd.Stopped:
                if (running)
                {
                    view = SetCurrentRunOutcome(view, RunOutcome.Abandoned);
                }

                return CloseOpenItems(view, runId: null, failed: true) with { Status = ConversationStatus.Stopped };

            case TurnEnd.Failed:
                if (running)
                {
                    view = SetCurrentRunOutcome(view, RunOutcome.Failed);
                }

                return CloseOpenItems(view, runId: null, failed: true) with
                {
                    Status = ConversationStatus.Failed,
                    Failure = ended.Failure ?? new RunFailure(FailureKind.Unexpected, "The turn failed."),
                };
        }

        if (running && view.OpenInterrupts.Any(i => i.RunId == run!.RunId))
        {
            // An approval-only interrupt reaches the client as ToolApprovalRequestContent, and no update
            // carries its RUN_FINISHED unless usage rides on it. The request itself proves the run ended.
            view = SetCurrentRunOutcome(view, RunOutcome.Interrupted);
        }
        else if (view.Failure is null && (run is null || running))
        {
            // AGUIChatClient doesn't require a terminal event before the stream ends. A run without one
            // didn't finish, whatever the transport says.
            if (running)
            {
                view = SetCurrentRunOutcome(view, RunOutcome.Failed);
            }

            return CloseOpenItems(view, runId: null, failed: true) with
            {
                Status = ConversationStatus.Failed,
                Failure = new RunFailure(FailureKind.StreamEnded, "The agent's stream ended before the run finished."),
            };
        }

        view = CloseOpenItems(view, runId: null, failed: view.Failure is not null);

        return view with
        {
            Status = view.Failure is not null ? ConversationStatus.Failed
                : view.OpenInterrupts.Any() ? ConversationStatus.AwaitingInput
                : view.CurrentRun?.Outcome == RunOutcome.Cancelled ? ConversationStatus.Cancelled
                : ConversationStatus.Idle,
        };
    }

    private static ConversationView SetCurrentRunOutcome(ConversationView view, RunOutcome outcome) =>
        view.CurrentRun is { } run
            ? view with { Runs = view.Runs.SetItem(view.Runs.Count - 1, run with { Outcome = outcome }) }
            : view;

    // Everything a run opened is closed when it ends. Text keeps what arrived; after a stop or a
    // failure it is marked partial. Calls without a result were either left for the client (the run
    // succeeded) or abandoned (it didn't).
    private static ConversationView CloseOpenItems(ConversationView view, string? runId, bool failed)
    {
        var messages = view.Messages;
        for (var i = 0; i < messages.Count; i++)
        {
            if (runId is not null && messages[i].RunId != runId)
            {
                continue;
            }

            messages = messages[i] switch
            {
                AssistantMessageView { IsStreaming: true } a => messages.SetItem(i, a with { IsStreaming = false, IsPartial = failed }),
                ReasoningMessageView { IsStreaming: true } r => messages.SetItem(i, r with { IsStreaming = false }),
                _ => messages,
            };
        }

        var pending = runId is null ? [] : view.Runs.Find(r => r.RunId == runId)?.PendingToolCallIds ?? [];
        var calls = view.ToolCalls;
        for (var i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            if ((runId is not null && call.RunId != runId) || call.Status is not (ToolCallStatus.Streaming or ToolCallStatus.InProgress or ToolCallStatus.AwaitingClient))
            {
                continue;
            }

            var status = failed ? ToolCallStatus.Abandoned
                : call.Status == ToolCallStatus.Streaming && !pending.Contains(call.Id) ? ToolCallStatus.Abandoned
                : ToolCallStatus.AwaitingClient;

            if (status != call.Status)
            {
                calls = calls.SetItem(i, call with { Status = status, Arguments = call.Arguments ?? JsonValues.TryParseObject(call.ArgumentsText) });
            }
        }

        return view with { Messages = messages, ToolCalls = calls, OpenChunks = ImmutableDictionary<string, string>.Empty };
    }

    // ---- Helpers shared by the slices -----------------------------------------------------------

    internal static ConversationView Warn(ConversationView view, string warning) =>
        view with { Warnings = Keep(view.Warnings.Add(warning)) };

    // A field the protocol describes, carrying a value its schema rejects, is fatal: the consumer fails
    // the run rather than repair or skip it (processing.mdx). The run is marked Failed with a
    // ProtocolViolation, and AgentConversation cancels the turn when it sees one.
    internal static ConversationView Violate(ConversationView view, string message)
    {
        if (view.Failure is { Kind: FailureKind.ProtocolViolation })
        {
            return view;
        }

        view = view with { Failure = new RunFailure(FailureKind.ProtocolViolation, message) };
        if (view.CurrentRun is not { Outcome: RunOutcome.Running } run)
        {
            return view;
        }

        view = view with { Runs = view.Runs.SetItem(view.Runs.Count - 1, run with { Outcome = RunOutcome.Failed }) };
        return CloseOpenItems(view, run.RunId, failed: true);
    }

    // After a violation, nothing more from the failed turn's stream is applied. (The values that can be
    // malformed all arrive on content-less updates, so no content is lost with them.)
    private static bool IsAbandoned(ConversationView view) =>
        view.Failure is { Kind: FailureKind.ProtocolViolation } && view.IsBusy;

    private static ImmutableList<T> Keep<T>(ImmutableList<T> list) =>
        list.Count > Retained ? list.RemoveRange(0, list.Count - Retained) : list;

    private static int IndexOfMessage(ConversationView view, string id) =>
        view.Messages.FindIndex(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    private static ConversationView ReplaceMessage(ConversationView view, int index, MessageView message) =>
        view with { Messages = view.Messages.SetItem(index, message) };

    private static ConversationView UpsertMessage(ConversationView view, MessageView message)
    {
        var index = IndexOfMessage(view, message.Id);
        return index < 0 ? view with { Messages = view.Messages.Add(message) } : ReplaceMessage(view, index, message);
    }

    private static string? RoleName(ChatRole? role) =>
        role is null ? null
        : role == ChatRole.User ? AGUIRoles.User
        : role == ChatRole.System ? AGUIRoles.System
        : role.Value.Value == AGUIRoles.Developer ? AGUIRoles.Developer
        : AGUIRoles.Assistant;
}
