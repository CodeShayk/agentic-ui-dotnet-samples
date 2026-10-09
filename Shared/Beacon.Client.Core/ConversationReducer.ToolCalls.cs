using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Beacon.Client.Core;

// Tool calls. Chapter 9 renders them; Chapter 10 adds the client's own tools.
//
// What the client sees depends on where it looks. On the wire a call is TOOL_CALL_START, ARGS
// deltas, END and, for a backend tool, TOOL_CALL_RESULT. AGUIChatClient yields nothing for START
// and ARGS and holds the call (as FunctionCallContent) until its result arrives, so without a
// WireEventTap a call appears complete, together with its result. Every step below is idempotent,
// because with a tap the same call is reported twice: once from the wire, once from the updates.
public static partial class ConversationReducer
{
    private static ConversationView StartToolCall(ConversationView view, string id, string name, string? parentMessageId, string? subagentRunId)
    {
        if (view.FindToolCall(id) is { } existing)
        {
            // An agreeing start may reopen a closed call; further arguments append.
            return existing.Status == ToolCallStatus.InProgress
                ? ReplaceCall(view, existing with { Status = ToolCallStatus.Streaming })
                : view;
        }

        // A call lives inside an assistant message. Without a parentMessageId, the call's own ID
        // names that message, which is what AGUIChatClient and other SDKs mint too.
        var parentId = string.IsNullOrEmpty(parentMessageId) ? id : parentMessageId;
        view = AttachCall(EnsureAssistantMessage(view, parentId, subagentRunId), parentId, id);

        var call = new ToolCallView(id, name)
        {
            ParentMessageId = parentId,
            RunId = view.CurrentRun?.RunId,
            SubagentRunId = subagentRunId,
        };

        return view with { ToolCalls = view.ToolCalls.Add(call) };
    }

    private static ConversationView AppendToolArguments(ConversationView view, string id, string? delta) =>
        view.FindToolCall(id) is { } call
            ? ReplaceCall(view, call with { ArgumentsText = call.ArgumentsText + delta })
            : Warn(view, $"Arguments for unknown tool call '{id}' were ignored.");

    // TOOL_CALL_END completes the arguments, not the call. Arguments are deliberately unvalidated on
    // the wire, so they are parsed here only if they happen to be a JSON object.
    private static ConversationView EndToolCall(ConversationView view, string id) =>
        view.FindToolCall(id) is { Status: ToolCallStatus.Streaming } call
            ? ReplaceCall(view, call with
            {
                Status = ToolCallStatus.InProgress,
                Arguments = JsonValues.TryParseObject(call.ArgumentsText),
            })
            : view;

    // TOOL_CALL_CHUNK, like TEXT_MESSAGE_CHUNK, reaches the client only as RawRepresentation. Note what
    // that means for a frontend tool: AGUIChatClient never makes a FunctionCallContent of it, so its
    // function-invoking layer can't run it.
    private static ConversationView ApplyToolCallChunk(ConversationView view, ToolCallChunkEvent e, string? lane)
    {
        var step = ResolveChunk(ref view, ChunkKind.Tool, ref lane, e.ToolCallId, out var id);
        if (ChunkViolation(step, "TOOL_CALL_CHUNK", "toolCallId") is { } violation)
        {
            return Violate(view, violation);
        }

        if (view.FindToolCall(id) is not { } call)
        {
            // The first chunk of a call must name the tool; the client never invents one.
            if (string.IsNullOrEmpty(e.ToolCallName))
            {
                return Violate(view, $"The first TOOL_CALL_CHUNK for '{id}' had no toolCallName.");
            }

            view = StartToolCall(view, id, e.ToolCallName, e.ParentMessageId, lane);
        }
        else if (!string.IsNullOrEmpty(e.ToolCallName) && call.Name.Length > 0 && e.ToolCallName != call.Name)
        {
            return Violate(view, $"A TOOL_CALL_CHUNK for '{id}' claimed tool '{e.ToolCallName}', but the call is '{call.Name}'.");
        }
        else if (!string.IsNullOrEmpty(e.ParentMessageId) && call.ParentMessageId is { } parent && parent != id && e.ParentMessageId != parent)
        {
            return Violate(view, $"A TOOL_CALL_CHUNK for '{id}' claimed parent message '{e.ParentMessageId}', but the call is in '{parent}'.");
        }

        return AppendToolArguments(view, id, e.Delta);
    }

    // FunctionCallContent: the call as Microsoft.Extensions.AI sees it, with parsed arguments and
    // InformationalOnly, which AGUIChatClient sets for every tool the client didn't declare itself.
    private static ConversationView UpsertCall(ConversationView view, FunctionCallContent content, string? messageId, string? subagentRunId)
    {
        var executor = content.InformationalOnly ? ToolExecutor.Agent : ToolExecutor.Client;
        var arguments = JsonValues.FromArguments(content.Arguments);

        if (view.FindToolCall(content.CallId) is { } existing)
        {
            return ReplaceCall(view, existing with
            {
                Name = existing.Name.Length > 0 ? existing.Name : content.Name,
                Executor = executor,
                Arguments = existing.Arguments ?? arguments,
                ArgumentsText = existing.ArgumentsText.Length > 0 ? existing.ArgumentsText : arguments?.GetRawText() ?? string.Empty,
                Status = existing.Status == ToolCallStatus.Streaming ? ToolCallStatus.InProgress : existing.Status,
            });
        }

        var parentId = string.IsNullOrEmpty(messageId) ? content.CallId : messageId;
        view = AttachCall(EnsureAssistantMessage(view, parentId, subagentRunId), parentId, content.CallId);

        var call = new ToolCallView(content.CallId, content.Name)
        {
            ParentMessageId = parentId,
            RunId = view.CurrentRun?.RunId,
            SubagentRunId = subagentRunId,
            Status = ToolCallStatus.InProgress,
            Executor = executor,
            Arguments = arguments,
            ArgumentsText = arguments?.GetRawText() ?? string.Empty,
        };

        return view with { ToolCalls = view.ToolCalls.Add(call) };
    }

    // TOOL_CALL_RESULT from the agent, or the FunctionResultContent the client's own function
    // invocation produced for a frontend tool. Either way the result becomes a tool message too.
    private static ConversationView CompleteToolCall(ConversationView view, string callId, string? resultMessageId, string content, string? error, string? subagentRunId)
    {
        if (view.FindToolCall(callId) is { } call)
        {
            view = ReplaceCall(view, call with
            {
                Status = ToolCallStatus.Completed,
                Result = content,
                Error = error,
                Arguments = call.Arguments ?? JsonValues.TryParseObject(call.ArgumentsText),
            });
        }
        else
        {
            // A result for a call this client never saw: one from before a restored history, say.
            view = view with
            {
                ToolCalls = view.ToolCalls.Add(new ToolCallView(callId, string.Empty)
                {
                    RunId = view.CurrentRun?.RunId,
                    SubagentRunId = subagentRunId,
                    Status = ToolCallStatus.Completed,
                    Result = content,
                    Error = error,
                }),
            };
        }

        var messageId = string.IsNullOrEmpty(resultMessageId) ? callId : resultMessageId;
        return UpsertMessage(view, new ToolMessageView(messageId, callId, content)
        {
            Error = error,
            RunId = view.CurrentRun?.RunId,
            SubagentRunId = subagentRunId,
        });
    }

    private static ConversationView EnsureAssistantMessage(ConversationView view, string id, string? subagentRunId) =>
        IndexOfMessage(view, id) >= 0
            ? view
            : view with
            {
                Messages = view.Messages.Add(new AssistantMessageView(id) { RunId = view.CurrentRun?.RunId, SubagentRunId = subagentRunId }),
            };

    private static ConversationView AttachCall(ConversationView view, string messageId, string callId)
    {
        var index = IndexOfMessage(view, messageId);
        return index >= 0 && view.Messages[index] is AssistantMessageView message && !message.ToolCallIds.Contains(callId)
            ? ReplaceMessage(view, index, message with { ToolCallIds = message.ToolCallIds.Add(callId) })
            : view;
    }

    private static ConversationView ReplaceCall(ConversationView view, ToolCallView call)
    {
        var index = view.ToolCalls.FindIndex(c => string.Equals(c.Id, call.Id, StringComparison.Ordinal));
        return view with { ToolCalls = index < 0 ? view.ToolCalls.Add(call) : view.ToolCalls.SetItem(index, call) };
    }

    // A frontend tool's result as the text that goes on the wire next run. AIFunctionFactory marshals
    // return values to JsonElement, which AGUIChatClient sends as raw JSON (a string arrives quoted), so
    // the view stores the same text and a snapshot restating it reconciles without a change.
    private static string ResultText(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement element => element.GetRawText(),
        _ => result.ToString() ?? string.Empty,
    };
}
