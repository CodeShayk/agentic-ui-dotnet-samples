using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Beacon.Client.Core;

// Reasoning messages and the encrypted values that travel with them. Chapter 16 builds the UI.
//
// AGUIChatClient turns REASONING_MESSAGE_CONTENT into TextReasoningContent and REASONING_ENCRYPTED_VALUE
// into TextReasoningContent with ProtectedData, both with the event as RawRepresentation. The span
// events and REASONING_MESSAGE_START, END and CHUNK arrive as content-less updates.
public static partial class ConversationReducer
{
    private static ConversationView OpenReasoning(ConversationView view, string id, string? subagentRunId)
    {
        var index = IndexOfMessage(view, id);
        if (index >= 0)
        {
            return view.Messages[index] is ReasoningMessageView existing
                ? ReplaceMessage(view, index, existing with { IsStreaming = true })
                : view;
        }

        return view with
        {
            Messages = view.Messages.Add(new ReasoningMessageView(id)
            {
                IsStreaming = true,
                RunId = view.CurrentRun?.RunId,
                SubagentRunId = subagentRunId,
            }),
        };
    }

    private static ConversationView AppendReasoning(ConversationView view, string id, string? delta, string? subagentRunId)
    {
        var index = IndexOfMessage(view, id);
        if (index < 0)
        {
            view = OpenReasoning(view, id, subagentRunId);
            index = view.Messages.Count - 1;
        }

        return string.IsNullOrEmpty(delta) ? view
            : view.Messages[index] is ReasoningMessageView reasoning ? ReplaceMessage(view, index, reasoning with { Text = reasoning.Text + delta })
            : Warn(view, $"Reasoning for '{id}', which isn't a reasoning message, was ignored.");
    }

    private static ConversationView CloseReasoning(ConversationView view, string id)
    {
        var index = IndexOfMessage(view, id);
        return index >= 0 && view.Messages[index] is ReasoningMessageView { IsStreaming: true } reasoning
            ? ReplaceMessage(view, index, reasoning with { IsStreaming = false })
            : view;
    }

    private static ConversationView ApplyReasoningChunk(ConversationView view, ReasoningMessageChunkEvent e, string? lane)
    {
        var step = ResolveChunk(ref view, ChunkKind.Reasoning, ref lane, e.MessageId, out var id);
        if (ChunkViolation(step, "REASONING_MESSAGE_CHUNK", "messageId") is { } violation)
        {
            return Violate(view, violation);
        }

        return AppendReasoning(step == ChunkStep.Open ? OpenReasoning(view, id, lane) : view, id, e.Delta, lane);
    }

    // REASONING_ENCRYPTED_VALUE: an opaque value the client stores with its entity and never reads.
    private static ConversationView AttachEncryptedValue(ConversationView view, ReasoningEncryptedValueEvent e)
    {
        if (e.Subtype == "tool-call")
        {
            return view.FindToolCall(e.EntityId) is { } call
                ? ReplaceCall(view, call with { EncryptedValue = e.EncryptedValue })
                : Warn(view, $"An encrypted value for unknown tool call '{e.EntityId}' was dropped.");
        }

        var index = IndexOfMessage(view, e.EntityId);
        return index >= 0
            ? ReplaceMessage(view, index, view.Messages[index] with { EncryptedValue = e.EncryptedValue })
            : Warn(view, $"An encrypted value for unknown message '{e.EntityId}' was dropped.");
    }

    // TextReasoningContent that didn't come from a reasoning event: an IChatClient layered over
    // AGUIChatClient produced it. Keyed by the update's message ID.
    private static ConversationView ApplyReasoningContent(ConversationView view, TextReasoningContent content, ChatResponseUpdate update, string? subagentRunId)
    {
        var id = update.MessageId ?? $"{update.ResponseId}-reasoning";
        if (!string.IsNullOrEmpty(content.Text))
        {
            view = AppendReasoning(view, id, content.Text, subagentRunId);
        }

        if (content.ProtectedData is { } protectedData && IndexOfMessage(view, id) is var index and >= 0)
        {
            view = ReplaceMessage(view, index, view.Messages[index] with { EncryptedValue = protectedData });
        }

        return view;
    }
}
