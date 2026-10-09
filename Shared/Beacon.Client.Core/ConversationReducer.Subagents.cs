using AGUI.Abstractions;

namespace Beacon.Client.Core;

// Steps (Chapter 13) and subagents (Chapter 18). Both arrive as content-less updates whose
// RawRepresentation is the event. Attribution of messages and tool calls to a subagent is done where
// those are created, from the event's subagentRunId or AGUIChatClient's "agui.subagentRunId" stamp.
public static partial class ConversationReducer
{
    private static ConversationView StartStep(ConversationView view, string name, string? subagentRunId) =>
        view with
        {
            Steps = view.Steps.Add(new StepView(name) { SubagentRunId = subagentRunId, RunId = view.CurrentRun?.RunId }),
        };

    // Steps match by name within their owner: a subagent may have a step with the parent's step name.
    private static ConversationView FinishStep(ConversationView view, string name, string? subagentRunId)
    {
        var index = view.Steps.FindLastIndex(s => s.IsActive && s.Name == name && s.SubagentRunId == subagentRunId);
        return index >= 0
            ? view with { Steps = view.Steps.SetItem(index, view.Steps[index] with { IsActive = false }) }
            : Warn(view, $"STEP_FINISHED for '{name}', which isn't active, was ignored.");
    }

    private static ConversationView StartSubagent(ConversationView view, SubagentStartedEvent e)
    {
        if (e.SubagentRunId is not { } id)
        {
            return view;
        }

        var index = view.Subagents.FindIndex(s => s.SubagentRunId == id);
        if (index < 0)
        {
            return view with
            {
                Subagents = view.Subagents.Add(new SubagentView(id, e.Name ?? string.Empty)
                {
                    Description = e.Description,
                    ParentSubagentRunId = e.ParentSubagentRunId,
                    ParentToolCallId = e.ParentToolCallId,
                    ParentMessageId = e.ParentMessageId,
                    RunId = view.CurrentRun?.RunId,
                }),
            };
        }

        var existing = view.Subagents[index];
        if (existing.Status == SubagentStatus.Running)
        {
            return Warn(view, $"SUBAGENT_STARTED for '{id}', which is already running, was ignored.");
        }

        // A later run that announces a suspended invocation's ID again is continuing it: the same entry
        // goes back to running, never a duplicate. Reusing a finished invocation's ID isn't a
        // continuation the spec describes, so the entry restarts with a warning.
        if (existing.Status is not (SubagentStatus.Suspended or SubagentStatus.Resumed))
        {
            view = Warn(view, $"SUBAGENT_STARTED reused '{id}', an invocation that had already ended.");
        }

        return view with
        {
            Subagents = view.Subagents.SetItem(index, existing with
            {
                Name = string.IsNullOrEmpty(e.Name) ? existing.Name : e.Name,
                Description = e.Description ?? existing.Description,
                Status = SubagentStatus.Running,
                Result = null,
                InterruptIds = [],
                Error = null,
                ErrorCode = null,
            }),
        };
    }

    // A suspended invocation stops waiting once the agent accepts answers to the interrupts it owns: its
    // suspended outcome's interruptIds, or the interrupts tagged with its ID. One suspended only because a
    // descendant interrupted resumes with that descendant. A continuation under the same ID then makes it
    // Running again; a producer that continues under a new ID leaves it Resumed (concepts/subagents.mdx).
    private static ConversationView ResumeSubagents(ConversationView view, IReadOnlyList<string> answered)
    {
        var subagents = view.Subagents;
        HashSet<string> resumed = new(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            for (var i = 0; i < subagents.Count; i++)
            {
                var subagent = subagents[i];
                if (subagent.Status != SubagentStatus.Suspended)
                {
                    continue;
                }

                List<string> owned = subagent.InterruptIds.Count > 0
                    ? [.. subagent.InterruptIds]
                    : [.. view.Interrupts.Where(x => x.SubagentRunId == subagent.SubagentRunId).Select(x => x.Id)];

                var children = subagents.Where(c => c.ParentSubagentRunId == subagent.SubagentRunId).ToList();
                var ready = owned.Count > 0
                    ? owned.Any(id => answered.Contains(id)) && owned.All(id => view.FindInterrupt(id) is not { IsAnswered: false })
                    : children.Any(c => resumed.Contains(c.SubagentRunId)) && !children.Any(c => c.Status == SubagentStatus.Suspended);

                if (ready)
                {
                    subagents = subagents.SetItem(i, subagent with { Status = SubagentStatus.Resumed });
                    resumed.Add(subagent.SubagentRunId);
                    changed = true;
                }
            }
        }
        while (changed);

        return resumed.Count == 0 ? view : view with { Subagents = subagents };
    }

    private static ConversationView FinishSubagent(ConversationView view, SubagentFinishedEvent e) =>
        UpdateSubagent(view, e.SubagentRunId, subagent => subagent with
        {
            Status = e.Outcome is SubagentFinishedSuspendedOutcome ? SubagentStatus.Suspended : SubagentStatus.Succeeded,
            Result = e.Result,
            InterruptIds = e.Outcome is SubagentFinishedSuspendedOutcome { InterruptIds: { } ids } ? [.. ids] : [],
        });

    // SUBAGENT_ERROR ends the invocation, not the run.
    private static ConversationView FailSubagent(ConversationView view, SubagentErrorEvent e) =>
        UpdateSubagent(view, e.SubagentRunId, subagent => subagent with
        {
            Status = SubagentStatus.Failed,
            Error = e.Message,
            ErrorCode = e.Code,
        });

    private static ConversationView UpdateSubagent(ConversationView view, string? id, Func<SubagentView, SubagentView> update)
    {
        var index = view.Subagents.FindIndex(s => s.SubagentRunId == id);
        return index >= 0
            ? view with { Subagents = view.Subagents.SetItem(index, update(view.Subagents[index])) }
            : Warn(view, $"An event ended subagent '{id}', which was never started.");
    }
}
