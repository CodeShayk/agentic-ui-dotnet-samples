using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Beacon.Client.Core;

// Interrupts: approvals (Chapter 14) and requests for input (Chapter 15).
//
// A run that stops to ask ends with RUN_FINISHED and an interrupt outcome. AGUIChatClient turns an
// interrupt about a buffered tool call into ToolApprovalRequestContent (wrapping the call) and any
// other interrupt into InterruptRequestContent. The view keeps the request content, because the
// answer goes back with it (AgentConversation.RespondAsync).
public static partial class ConversationReducer
{
    private static ConversationView AddApprovalRequest(ConversationView view, ToolApprovalRequestContent approval, string? messageId, string? subagentRunId)
    {
        var interrupt = approval.RawRepresentation as AGUIInterrupt;

        if (approval.ToolCall is FunctionCallContent call)
        {
            view = UpsertCall(view, call, messageId, subagentRunId);
            if (view.FindToolCall(call.CallId) is { } pending)
            {
                // The agent runs the tool once it is approved: it is the agent's call, waiting on a person.
                view = ReplaceCall(view, pending with { Status = ToolCallStatus.AwaitingApproval, Executor = ToolExecutor.Agent });
            }
        }

        return UpsertInterrupt(view, new InterruptView(approval.RequestId, InterruptKind.ToolApproval)
        {
            Reason = interrupt?.Reason ?? InterruptReasons.ToolCall,
            Message = interrupt?.Message,
            ToolCallId = approval.ToolCall.CallId,
            ResponseSchema = interrupt?.ResponseSchema,
            ExpiresAt = interrupt?.ExpiresAt,
            Metadata = interrupt?.Metadata,
            RunId = view.CurrentRun?.RunId,
            SubagentRunId = interrupt?.SubagentRunId ?? subagentRunId,
            Request = approval,
        });
    }

    private static ConversationView AddInterruptRequest(ConversationView view, InterruptRequestContent request, string? subagentRunId) =>
        UpsertInterrupt(view, new InterruptView(request.RequestId, InterruptKind.Input)
        {
            Reason = request.Reason,
            Message = request.Message,
            ToolCallId = request.ToolCallId,
            ResponseSchema = request.ResponseSchema,
            ExpiresAt = request.ExpiresAt,
            Metadata = request.Metadata,
            RunId = view.CurrentRun?.RunId,
            SubagentRunId = subagentRunId,
            Request = request,
        });

    // From the RUN_FINISHED outcome itself, when an update carries it.
    private static InterruptView FromInterrupt(AGUIInterrupt interrupt, string runId) =>
        new(interrupt.Id, string.Equals(interrupt.Reason, InterruptReasons.ToolCall, StringComparison.OrdinalIgnoreCase) && interrupt.ToolCallId is not null
            ? InterruptKind.ToolApproval
            : InterruptKind.Input)
        {
            Reason = interrupt.Reason,
            Message = interrupt.Message,
            ToolCallId = interrupt.ToolCallId,
            ResponseSchema = interrupt.ResponseSchema,
            ExpiresAt = interrupt.ExpiresAt,
            Metadata = interrupt.Metadata,
            RunId = runId,
            SubagentRunId = interrupt.SubagentRunId,
        };

    // The same interrupt can be reported twice (by its content and by RUN_FINISHED); merge, never duplicate.
    // A later run that ends with an interrupt of the same ID holds it open again: the producer kept it.
    private static ConversationView UpsertInterrupt(ConversationView view, InterruptView interrupt)
    {
        var index = view.Interrupts.FindIndex(i => i.Id == interrupt.Id);
        if (index < 0)
        {
            return view with { Interrupts = view.Interrupts.Add(interrupt) };
        }

        var existing = view.Interrupts[index];
        if (existing.IsAnswered && interrupt.RunId is { } raisedIn && raisedIn != existing.RunId)
        {
            return view with { Interrupts = view.Interrupts.SetItem(index, interrupt with { Request = interrupt.Request ?? existing.Request }) };
        }

        return view with
        {
            Interrupts = view.Interrupts.SetItem(index, existing with
            {
                Reason = existing.Reason ?? interrupt.Reason,
                Message = existing.Message ?? interrupt.Message,
                ToolCallId = existing.ToolCallId ?? interrupt.ToolCallId,
                ResponseSchema = existing.ResponseSchema ?? interrupt.ResponseSchema,
                ExpiresAt = existing.ExpiresAt ?? interrupt.ExpiresAt,
                Metadata = existing.Metadata ?? interrupt.Metadata,
                SubagentRunId = existing.SubagentRunId ?? interrupt.SubagentRunId,
                Request = existing.Request ?? interrupt.Request,
            }),
        };
    }

    // The agent accepted the answers (the resuming run started), so the interrupts close, and any
    // subagent that was waiting on them stops waiting.
    private static ConversationView MarkAnswered(ConversationView view, IReadOnlyList<string> ids)
    {
        var interrupts = view.Interrupts;
        for (var i = 0; i < interrupts.Count; i++)
        {
            if (!interrupts[i].IsAnswered && ids.Contains(interrupts[i].Id))
            {
                interrupts = interrupts.SetItem(i, interrupts[i] with { IsAnswered = true, LastAnswerFailure = null });
            }
        }

        view = ResumeSubagents(view with { Interrupts = interrupts }, ids);
        return view.Status == ConversationStatus.AwaitingInput && !view.OpenInterrupts.Any()
            ? view with { Status = ConversationStatus.Idle }
            : view;
    }

    // Answers whose resuming run never started: the interrupts stay open, and each records why.
    private static ConversationView RecordUnaccepted(ConversationView view, IReadOnlyList<string> ids, RunFailure failure)
    {
        var interrupts = view.Interrupts;
        for (var i = 0; i < interrupts.Count; i++)
        {
            if (!interrupts[i].IsAnswered && ids.Contains(interrupts[i].Id))
            {
                interrupts = interrupts.SetItem(i, interrupts[i] with { LastAnswerFailure = failure });
            }
        }

        return view with { Interrupts = interrupts };
    }
}
