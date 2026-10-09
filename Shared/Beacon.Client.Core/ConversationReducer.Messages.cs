using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Beacon.Client.Core;

// Text messages: the user's own, the agent's streamed ones and the compact chunk form.
public static partial class ConversationReducer
{
    private static ConversationView AddSentMessage(ConversationView view, ChatMessage message)
    {
        if (message.MessageId is not { Length: > 0 } id || IndexOfMessage(view, id) >= 0)
        {
            return view; // no ID to key it by, or already in the transcript
        }

        MessageView sent = message.Role == ChatRole.User
            ? new UserMessageView(id, message.Text)
            {
                Name = message.AuthorName,
                Attachments = [.. message.Contents.Where(c => c is not TextContent)],
            }
            : new SystemMessageView(id, message.Text, IsDeveloper: message.Role != ChatRole.System);

        return view with { Messages = view.Messages.Add(sent) };
    }

    // TEXT_MESSAGE_START, or the first content for a message whose start the client never saw.
    private static ConversationView OpenMessage(ConversationView view, string id, string? role, string? name, string? subagentRunId)
    {
        var index = IndexOfMessage(view, id);
        if (index >= 0)
        {
            // A closed message may be reopened by an agreeing start; more content appends.
            return view.Messages[index] is AssistantMessageView existing
                ? ReplaceMessage(view, index, existing with { IsStreaming = true })
                : view;
        }

        MessageView opened = role switch
        {
            AGUIRoles.User => new UserMessageView(id, string.Empty),
            AGUIRoles.System => new SystemMessageView(id, string.Empty, IsDeveloper: false),
            AGUIRoles.Developer => new SystemMessageView(id, string.Empty, IsDeveloper: true),
            _ => new AssistantMessageView(id) { Name = name, IsStreaming = true }, // an absent role means assistant
        };

        return view with
        {
            Messages = view.Messages.Add(opened with { RunId = view.CurrentRun?.RunId, SubagentRunId = subagentRunId }),
        };
    }

    // TEXT_MESSAGE_CONTENT: deltas concatenate in arrival order.
    private static ConversationView AppendText(ConversationView view, string id, string? delta, string? role, string? subagentRunId)
    {
        var index = IndexOfMessage(view, id);
        if (index < 0)
        {
            view = OpenMessage(view, id, role, name: null, subagentRunId);
            index = view.Messages.Count - 1;
        }

        if (string.IsNullOrEmpty(delta))
        {
            return view;
        }

        return view.Messages[index] switch
        {
            AssistantMessageView a => ReplaceMessage(view, index, a with { Text = a.Text + delta }),
            UserMessageView u => ReplaceMessage(view, index, u with { Text = u.Text + delta }),
            SystemMessageView s => ReplaceMessage(view, index, s with { Text = s.Text + delta }),
            var other => Warn(view, $"Text for '{id}', a {other.Role} message, was ignored."),
        };
    }

    // TEXT_MESSAGE_END. Without a WireEventTap the client never sees it, and the run's end closes the message instead.
    private static ConversationView CloseMessage(ConversationView view, string id)
    {
        var index = IndexOfMessage(view, id);
        return index >= 0 && view.Messages[index] is AssistantMessageView { IsStreaming: true } a
            ? ReplaceMessage(view, index, a with { IsStreaming = false })
            : view;
    }

    // TEXT_MESSAGE_CHUNK reaches the client only as RawRepresentation: AGUIChatClient 1.0.0 doesn't turn
    // it into TextContent, so update.Text never shows it. The reducer expands it into the triad itself.
    private static ConversationView ApplyTextChunk(ConversationView view, TextMessageChunkEvent e, string? lane)
    {
        var step = ResolveChunk(ref view, ChunkKind.Text, ref lane, e.MessageId, out var id);
        if (ChunkViolation(step, "TEXT_MESSAGE_CHUNK", "messageId") is { } violation)
        {
            return Violate(view, violation);
        }

        // A continuation may repeat its opener's role or name, but only with the same value; an
        // opener without a role opened an assistant message.
        if (view.FindMessage(id) is { } opened)
        {
            if (e.Role is { } role && role != opened.Role)
            {
                return Violate(view, $"A TEXT_MESSAGE_CHUNK for '{id}' claimed role '{role}', but the message is '{opened.Role}'.");
            }

            if (e.Name is { } name && opened is AssistantMessageView { Name: { } openedName } && openedName != name)
            {
                return Violate(view, $"A TEXT_MESSAGE_CHUNK for '{id}' claimed name '{name}', but the message is '{openedName}'.");
            }
        }

        if (step == ChunkStep.Open)
        {
            view = OpenMessage(view, id, e.Role, e.Name, lane);
        }

        return AppendText(view, id, e.Delta, e.Role, lane);
    }

    // ---- Chunk streams ------------------------------------------------------------------------

    private enum ChunkKind
    {
        Text,
        Tool,
        Reasoning,
    }

    private enum ChunkStep
    {
        Open,
        Continue,
        MissingId,
        Ambiguous,
    }

    private static string ChunkKey(ChunkKind kind, string? lane) => $"{kind}:{lane}";

    // The spec's chunk rules ("The first chunk" and "Closing a chunk stream" in streaming.mdx). A chunk
    // with an ID opens that item, or continues it if it is the lane's open item; naming a different ID
    // closes the lane's open item first. A chunk without an ID continues its lane's open item; an
    // untagged one with nothing open in its own lane continues the only open item of its kind, wherever
    // it is (and takes that item's lane). Anything else is a first chunk without its ID, or a
    // continuation with no unique referent: both protocol violations, never a guess.
    private static ChunkStep ResolveChunk(ref ConversationView view, ChunkKind kind, ref string? lane, string? chunkId, out string id)
    {
        var key = ChunkKey(kind, lane);
        view.OpenChunks.TryGetValue(key, out var open);

        if (chunkId is null)
        {
            id = open ?? string.Empty;
            if (open is not null)
            {
                return ChunkStep.Continue;
            }

            var prefix = ChunkKey(kind, lane: null);
            var candidates = lane is null ? view.OpenChunks.Where(c => c.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList() : [];
            if (candidates.Count != 1)
            {
                return candidates.Count == 0 ? ChunkStep.MissingId : ChunkStep.Ambiguous;
            }

            (lane, id) = (candidates[0].Key[prefix.Length..], candidates[0].Value);
            return ChunkStep.Continue;
        }

        id = chunkId;
        if (open == chunkId)
        {
            return ChunkStep.Continue;
        }

        if (open is not null)
        {
            view = CloseChunk(view, key, open);
        }

        view = view with { OpenChunks = view.OpenChunks.SetItem(key, chunkId) };
        return ChunkStep.Open;
    }

    private static string? ChunkViolation(ChunkStep step, string eventType, string idField) => step switch
    {
        ChunkStep.MissingId => $"A {eventType} had no {idField}, and no open {eventType} stream could continue it.",
        ChunkStep.Ambiguous => $"A {eventType} without {idField} or subagentRunId could continue more than one open stream.",
        _ => null,
    };

    // The chunked form has no end events; the stream "can't continue" at these points (the spec's
    // "Closing a chunk stream"): a run-level event closes every lane, a subagent's terminal closes its
    // lane, and most other events close the open chunks of their own lane.
    private static ConversationView CloseChunkStreams(ConversationView view, BaseEvent evt, string? attributedTo)
    {
        if (view.OpenChunks.IsEmpty)
        {
            return view;
        }

        switch (evt)
        {
            case RunStartedEvent or RunFinishedEvent or RunErrorEvent or MessagesSnapshotEvent:
                foreach (var (key, id) in view.OpenChunks)
                {
                    view = CloseChunk(view, key, id);
                }

                return view;

            case RawEvent or ActivitySnapshotEvent or ActivityDeltaEvent or ReasoningEncryptedValueEvent or SubagentStartedEvent:
                return view;

            case SubagentFinishedEvent finished:
                return CloseLane(view, finished.SubagentRunId, except: null);

            case SubagentErrorEvent failed:
                return CloseLane(view, failed.SubagentRunId, except: null);

            case TextMessageChunkEvent chunk:
                return CloseLane(view, chunk.SubagentRunId ?? attributedTo, except: ChunkKind.Text);

            case ToolCallChunkEvent chunk:
                return CloseLane(view, chunk.SubagentRunId ?? attributedTo, except: ChunkKind.Tool);

            case ReasoningMessageChunkEvent chunk:
                return CloseLane(view, chunk.SubagentRunId ?? attributedTo, except: ChunkKind.Reasoning);

            default:
                return CloseLane(view, LaneOf(evt) ?? attributedTo, except: null);
        }
    }

    private static ConversationView CloseLane(ConversationView view, string? lane, ChunkKind? except)
    {
        foreach (var kind in Enum.GetValues<ChunkKind>())
        {
            var key = ChunkKey(kind, lane);
            if (kind != except && view.OpenChunks.TryGetValue(key, out var id))
            {
                view = CloseChunk(view, key, id);
            }
        }

        return view;
    }

    // Synthesizes the end event the chunked form never sends.
    private static ConversationView CloseChunk(ConversationView view, string key, string id)
    {
        view = view with { OpenChunks = view.OpenChunks.Remove(key) };
        return key.StartsWith(nameof(ChunkKind.Text), StringComparison.Ordinal) ? CloseMessage(view, id)
            : key.StartsWith(nameof(ChunkKind.Tool), StringComparison.Ordinal) ? EndToolCall(view, id)
            : CloseReasoning(view, id);
    }

    private static string? LaneOf(BaseEvent evt) => evt switch
    {
        TextMessageStartEvent e => e.SubagentRunId,
        TextMessageContentEvent e => e.SubagentRunId,
        TextMessageEndEvent e => e.SubagentRunId,
        ToolCallStartEvent e => e.SubagentRunId,
        ToolCallArgsEvent e => e.SubagentRunId,
        ToolCallEndEvent e => e.SubagentRunId,
        ToolCallResultEvent e => e.SubagentRunId,
        StateSnapshotEvent e => e.SubagentRunId,
        StateDeltaEvent e => e.SubagentRunId,
        StepStartedEvent e => e.SubagentRunId,
        StepFinishedEvent e => e.SubagentRunId,
        CustomEvent e => e.SubagentRunId,
        ReasoningStartEvent e => e.SubagentRunId,
        ReasoningEndEvent e => e.SubagentRunId,
        ReasoningMessageStartEvent e => e.SubagentRunId,
        ReasoningMessageContentEvent e => e.SubagentRunId,
        ReasoningMessageEndEvent e => e.SubagentRunId,
        _ => null,
    };
}
