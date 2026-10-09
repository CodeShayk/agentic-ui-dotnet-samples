using AGUI.Abstractions;
using Ch23.Beacon.Security.Approvals;
using Ch23.Beacon.Security.Identity;
using Ch23.Beacon.Security.Logging;
using Microsoft.Extensions.Logging;

namespace Ch23.Beacon.Security.History;

/// <summary>Why a run was refused before it started, as an HTTP problem.</summary>
public sealed record RunRejection(int StatusCode, string Title, string Detail);

/// <summary>
/// A run the server agreed to: the input it will actually run (with no resume list), the client's new
/// messages, the approvals the gate decided, the grants the tools of this request may spend, and whether
/// the client's copy differed from the server's (then the run restates the conversation, as in Chapter 22).
/// </summary>
public sealed record RunPlan(
    RunAgentInput Input,
    IReadOnlyList<AGUIMessage> AcceptedMessages,
    IReadOnlyList<PendingApproval> Decided,
    ApprovalGrants Grants,
    bool Diverged);

/// <summary>
/// Turns what a client sent into what the server will run. Chapter 14's resume gate checks every answer
/// against approvals this user was asked; then Chapter 22's rule applies (the server's copy is the history,
/// the client's copy only locates the client and supplies what's new), with two additions: the history is
/// this user's, and only what a client may author gets in, user turns and results for frontend calls the
/// server is waiting on. The transcript is linear: a run continues the latest run or none.
/// </summary>
public sealed class RunReconciler(ResumeGate gate, ILogger<RunReconciler> logger)
{
    public (RunPlan? Plan, RunRejection? Rejection) Reconcile(
        BeaconCaller caller,
        RunAgentInput input,
        Conversation conversation,
        ApprovalRun run)
    {
        // 0. One linear history per user and thread: a run may name the latest run as its parent, nothing else.
        if (input.ParentRunId is { Length: > 0 } parent && parent != conversation.LastRunId)
        {
            return Refuse(409, "Not the latest run", $"Thread {input.ThreadId} continues from its latest run; this endpoint doesn't branch.");
        }

        // 1. Answers: each must name an approval this user was asked on this thread, exactly as proposed (or as
        //    an edit the policy accepts), once and in time, and every open approval needs one. All or nothing. An exact
        //    repeat is taken over only while the transcript holds no result for its call.
        var (decided, rejection) = gate.Check(input, run, conversation.HasResultFor);
        if (rejection is not null)
        {
            return Refuse(rejection.StatusCode, rejection.Title, rejection.Detail);
        }

        // 2. Messages: the server's copy of everything it already has, plus what is genuinely new.
        var messages = input.Messages ?? [];
        var lastKnown = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            if (conversation.TryGetMessage(messages[i].Id, out _))
            {
                lastKnown = i;
            }
        }

        var accepted = new List<AGUIMessage>();
        var diverged = false;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (conversation.TryGetMessage(message.Id, out var serverCopy))
            {
                if (!SameText(serverCopy, message))
                {
                    SecurityLog.IgnoredChangedCopy(logger, message.Id, conversation.ThreadId, caller.UserId);
                    diverged = true;
                }

                continue;
            }

            switch (message)
            {
                // New user turns come after everything the server knows; a "user" message slipped
                // into the middle of the history is an edit, not a turn.
                case AGUIUserMessage user when i > lastKnown:
                    accepted.Add(new AGUIUserMessage { Id = user.Id, Content = user.Content });
                    break;

                // A frontend tool result is accepted only for a call the server is waiting on, once.
                case AGUIToolMessage tool when i > lastKnown
                                               && tool.ToolCallId is { } callId
                                               && conversation.PendingClientCalls.ContainsKey(callId)
                                               && !accepted.OfType<AGUIToolMessage>().Any(t => t.ToolCallId == callId):
                    accepted.Add(new AGUIToolMessage { Id = tool.Id, ToolCallId = callId, Content = tool.Content });
                    break;

                default:
                    SecurityLog.IgnoredUnknownMessage(logger, message.Role, message.Id, conversation.ThreadId, caller.UserId);
                    diverged = true;
                    break;
            }
        }

        var plan = new RunPlan(
            new RunAgentInput
            {
                ThreadId = input.ThreadId,
                RunId = input.RunId,
                ParentRunId = input.ParentRunId,
                ProtocolVersion = input.ProtocolVersion,
                Messages = [.. conversation.Messages, .. accepted],
                Tools = input.Tools,
                Context = input.Context,
                State = input.State,
                // No resume list: the SDK would turn any entry into an approved call. The handler adds the
                // server's own approval exchange instead (Chapter 14). forwardedProps are dropped as unused.
            },
            accepted,
            decided,
            new ApprovalGrants(decided),
            diverged);

        return (plan, null);

        (RunPlan?, RunRejection?) Refuse(int status, string title, string detail)
        {
            SecurityLog.RefusedRun(logger, conversation.ThreadId, caller.UserId, title);
            return (null, new RunRejection(status, title, detail));
        }
    }

    private static bool SameText(AGUIMessage serverCopy, AGUIMessage clientCopy) => (serverCopy, clientCopy) switch
    {
        (AGUIUserMessage s, AGUIUserMessage c) => s.Content.ToString() == c.Content.ToString(),
        (AGUIAssistantMessage s, AGUIAssistantMessage c) =>
            (s.Content ?? string.Empty) == (c.Content ?? string.Empty)
            && (s.ToolCalls ?? []).Select(t => t.Id).SequenceEqual((c.ToolCalls ?? []).Select(t => t.Id)),
        (AGUIToolMessage s, AGUIToolMessage c) => s.Content.ToString() == c.Content.ToString(),
        _ => serverCopy.Role == clientCopy.Role,
    };
}
