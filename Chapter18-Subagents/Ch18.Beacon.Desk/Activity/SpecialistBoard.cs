using System.Globalization;
using Beacon.Client.Core;

namespace Ch18.Beacon.Desk.Activity;

/// <summary>What a person should read from a lane's status chip.</summary>
public enum LaneStatus
{
    /// <summary>Started and still working.</summary>
    Running,

    /// <summary>Finished with success.</summary>
    Done,

    /// <summary>Suspended, waiting on an approval nobody has answered yet.</summary>
    WaitingForApproval,

    /// <summary>
    /// Was waiting, and the agent accepted the answer (<see cref="SubagentStatus.Resumed"/>). A continuation under
    /// the same ID makes it <see cref="Running"/> again; a producer that continues under a new ID leaves it here.
    /// </summary>
    Resuming,

    /// <summary>Suspended, with no open interrupt this client knows of.</summary>
    Suspended,

    /// <summary>Ended with <c>SUBAGENT_ERROR</c>. The coordinator carried on without it.</summary>
    Failed,

    /// <summary>Events carried its <c>subagentRunId</c>, but the producer never announced it. Its state is unknown.</summary>
    Unannounced,
}

/// <summary>One thing a lane shows, attributed to the invocation that produced it.</summary>
/// <param name="Id">The message or tool call ID.</param>
/// <param name="Owner">Who produced it, as a person reads it: "Parts specialist".</param>
public abstract record LaneEntry(string Id, string Owner);

/// <summary>A message the subagent wrote.</summary>
public sealed record LaneMessage(string Id, string Owner, string Text, bool IsStreaming) : LaneEntry(Id, Owner);

/// <summary>A tool call the subagent made, with its result once there is one.</summary>
public sealed record LaneToolCall(string Id, string Owner, ToolCallView Call) : LaneEntry(Id, Owner);

/// <summary>One subagent invocation, as a lane: who, how it's going, what it did, and what it waits on.</summary>
/// <param name="SubagentRunId">The invocation's ID: the lane's key, and nothing a person needs to read.</param>
/// <param name="Name">The subagent's reusable name, such as <c>parts-specialist</c>.</param>
/// <param name="Label">The name as a person reads it.</param>
public sealed record SpecialistLane(string SubagentRunId, string Name, string Label)
{
    public string? Description { get; init; }

    public LaneStatus Status { get; init; }

    /// <summary>The <c>SUBAGENT_ERROR</c> message, for a failed lane.</summary>
    public string? Error { get; init; }

    /// <summary>The tool call that spawned the invocation (agents as tools), when the producer said so.</summary>
    public string? SpawnedBy { get; init; }

    /// <summary>The run the invocation started in.</summary>
    public string? RunId { get; init; }

    /// <summary>Messages and tool calls, in the order they arrived.</summary>
    public IReadOnlyList<LaneEntry> Entries { get; init; } = [];

    /// <summary>The interrupts this invocation raised that are still waiting for an answer.</summary>
    public IReadOnlyList<InterruptView> OpenInterrupts { get; init; } = [];

    /// <summary>Invocations this one delegated to (<c>parentSubagentRunId</c>).</summary>
    public IReadOnlyList<SpecialistLane> Children { get; init; } = [];
}

/// <summary>The lanes that started in one run.</summary>
public sealed record ActivityRun(string RunId, IReadOnlyList<SpecialistLane> Lanes);

/// <summary>One of the coordinator's own steps in the current run.</summary>
public sealed record CoordinatorStep(string Name, string Label, bool IsActive);

/// <summary>
/// The Desk's specialist board, projected from the conversation's view state: the coordinator's steps for
/// the current run, and a lane per subagent invocation, grouped by the run it started in. UI-free, so the
/// same projection can drive Beacon Field and be tested without a renderer.
/// </summary>
public sealed record SpecialistBoard(IReadOnlyList<CoordinatorStep> Steps, IReadOnlyList<ActivityRun> Runs)
{
    public static SpecialistBoard Empty { get; } = new([], []);

    public bool IsEmpty => Runs.Count == 0;

    public static SpecialistBoard From(ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        // Lanes come from the Subagents slice. An ID that tags messages or calls without ever being announced
        // still gets a lane: attribution alone is a valid way to report subagents, and grouping must not need it.
        var announced = view.Subagents.Select(s => s.SubagentRunId).ToHashSet(StringComparer.Ordinal);
        var unannounced = view.Messages.Select(m => m.SubagentRunId)
            .Concat(view.ToolCalls.Select(c => c.SubagentRunId))
            .OfType<string>()
            .Where(id => !announced.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .Select(id => new SubagentView(id, id) { RunId = RunOf(view, id) });

        var all = view.Subagents.Concat(unannounced).ToList();
        var lanes = all.ToDictionary(s => s.SubagentRunId, s => Lane(view, s, announced.Contains(s.SubagentRunId)), StringComparer.Ordinal);

        // Nesting follows parentSubagentRunId; a child whose parent this client never saw stays at the top.
        SpecialistLane WithChildren(SubagentView subagent) => lanes[subagent.SubagentRunId] with
        {
            Children = [.. all.Where(c => c.ParentSubagentRunId == subagent.SubagentRunId).Select(WithChildren)],
        };

        var roots = all.Where(s => s.ParentSubagentRunId is null || !lanes.ContainsKey(s.ParentSubagentRunId)).Select(WithChildren).ToList();
        var runs = roots
            .GroupBy(l => l.RunId ?? string.Empty)
            .Select(g => new ActivityRun(g.Key, [.. g]))
            .ToList();

        return new SpecialistBoard(StepsOf(view), runs);
    }

    /// <summary>"parts-specialist" becomes "Parts specialist".</summary>
    public static string LabelFor(string name)
    {
        var words = name.Replace('-', ' ').Replace('_', ' ').Trim();
        return words.Length == 0 ? name : char.ToUpper(words[0], CultureInfo.InvariantCulture) + words[1..];
    }

    private static SpecialistLane Lane(ConversationView view, SubagentView subagent, bool announced)
    {
        var label = LabelFor(subagent.Name);
        List<InterruptView> waitingOn = [.. view.OpenInterrupts.Where(i => i.SubagentRunId == subagent.SubagentRunId || subagent.InterruptIds.Contains(i.Id))];
        return new SpecialistLane(subagent.SubagentRunId, subagent.Name, label)
        {
            Description = subagent.Description,
            Status = announced ? StatusOf(subagent, waitingOn) : LaneStatus.Unannounced,
            Error = subagent.Error,
            SpawnedBy = subagent.ParentToolCallId is { } callId ? view.FindToolCall(callId)?.Name ?? callId : null,
            RunId = subagent.RunId,
            Entries = EntriesOf(view, subagent.SubagentRunId, label),
            OpenInterrupts = waitingOn,
        };
    }

    // Beacon.Client.Core tracks the whole life of an invocation, continuation included, so the status is a plain reading.
    private static LaneStatus StatusOf(SubagentView subagent, IReadOnlyList<InterruptView> openInterrupts) => subagent.Status switch
    {
        SubagentStatus.Running => LaneStatus.Running,
        SubagentStatus.Succeeded => LaneStatus.Done,
        SubagentStatus.Failed => LaneStatus.Failed,
        SubagentStatus.Resumed => LaneStatus.Resuming,
        SubagentStatus.Suspended when openInterrupts.Count > 0 => LaneStatus.WaitingForApproval,
        _ => LaneStatus.Suspended,
    };

    private static IReadOnlyList<LaneEntry> EntriesOf(ConversationView view, string subagentRunId, string owner)
    {
        List<LaneEntry> entries = [];
        HashSet<string> shown = new(StringComparer.Ordinal);

        foreach (var message in view.Messages)
        {
            if (message is not AssistantMessageView assistant || assistant.SubagentRunId != subagentRunId)
            {
                continue;
            }

            if (assistant.Text.Length > 0)
            {
                entries.Add(new LaneMessage(assistant.Id, owner, assistant.Text, assistant.IsStreaming));
            }

            foreach (var call in view.ToolCallsOf(assistant).Where(c => c.SubagentRunId == subagentRunId && shown.Add(c.Id)))
            {
                entries.Add(new LaneToolCall(call.Id, owner, call));
            }
        }

        // A call whose message this client never saw (a result restored from history, say) still belongs here.
        entries.AddRange(view.ToolCalls.Where(c => c.SubagentRunId == subagentRunId && shown.Add(c.Id)).Select(c => new LaneToolCall(c.Id, owner, c)));
        return entries;
    }

    private static IReadOnlyList<CoordinatorStep> StepsOf(ConversationView view)
    {
        var runId = view.CurrentRun?.RunId;
        return [.. view.Steps
            .Where(s => s.SubagentRunId is null && s.RunId == runId)
            .Select(s => new CoordinatorStep(s.Name, LabelFor(s.Name), s.IsActive))];
    }

    private static string? RunOf(ConversationView view, string subagentRunId) =>
        view.Messages.FirstOrDefault(m => m.SubagentRunId == subagentRunId)?.RunId
        ?? view.ToolCalls.FirstOrDefault(c => c.SubagentRunId == subagentRunId)?.RunId;
}
