using System.Text;
using System.Text.Json.Nodes;
using Beacon.Client.Core;

namespace Ch13.Beacon.Desk.Progress;

/// <summary>One step of the agent's plan, as the checklist shows it.</summary>
public sealed record PlanStepView(string Title, PlanStepStatus Status, string? Note);

public enum PlanStepStatus
{
    Pending,
    InProgress,
    Done,
}

/// <summary>
/// The agent's diagnostic plan, projected from <c>state.plan</c>. Equality is by value, steps included,
/// so a component can tell whether the plan it shows has changed.
/// </summary>
/// <param name="IsRunning">False once the turn has ended: a step still in progress then was cut off.</param>
public sealed record PlanView(string? WorkOrderId, IReadOnlyList<PlanStepView> Steps, bool IsRunning)
{
    public int DoneCount => Steps.Count(s => s.Status == PlanStepStatus.Done);

    public PlanStepView? Current => Steps.FirstOrDefault(s => s.Status == PlanStepStatus.InProgress);

    public bool Equals(PlanView? other) =>
        other is not null && WorkOrderId == other.WorkOrderId && IsRunning == other.IsRunning && Steps.SequenceEqual(other.Steps);

    public override int GetHashCode() => HashCode.Combine(WorkOrderId, Steps.Count, DoneCount);

    /// <summary>Reads the plan from the shared state. Agent-written state is untrusted: anything off-shape is skipped.</summary>
    public static PlanView? From(ConversationView view)
    {
        if (view.State.Document?["plan"] is not JsonObject plan || plan["steps"] is not JsonArray steps)
        {
            return null;
        }

        List<PlanStepView> items = [];
        foreach (var step in steps.OfType<JsonObject>())
        {
            items.Add(new PlanStepView(
                Text(step["title"]) ?? "(untitled step)",
                Text(step["status"]) switch
                {
                    "in_progress" => PlanStepStatus.InProgress,
                    "done" => PlanStepStatus.Done,
                    _ => PlanStepStatus.Pending, // unknown statuses read as not started
                },
                Text(step["note"])));
        }

        return new PlanView(Text(plan["workOrderId"]), items, view.IsBusy);
    }

    internal static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

public enum ReportStatus
{
    Drafting,
    Saving,
    Saved,
    Rejected,
}

/// <summary>The service report as the preview shows it, projected from <c>state.report</c>.</summary>
/// <param name="IsStopped">The run ended before the report was saved: what's there is a partial draft.</param>
public sealed record ReportView(
    string? WorkOrderId,
    ReportStatus Status,
    string Summary,
    string Findings,
    string Recommendation,
    string? Error,
    bool IsStopped)
{
    public static ReportView? From(ConversationView view)
    {
        if (view.State.Document?["report"] is not JsonObject report)
        {
            return null;
        }

        var status = PlanView.Text(report["status"]) switch
        {
            "saving" => ReportStatus.Saving,
            "saved" => ReportStatus.Saved,
            "rejected" => ReportStatus.Rejected,
            _ => ReportStatus.Drafting,
        };

        return new ReportView(
            PlanView.Text(report["workOrderId"]),
            status,
            PlanView.Text(report["summary"]) ?? string.Empty,
            PlanView.Text(report["findings"]) ?? string.Empty,
            PlanView.Text(report["recommendation"]) ?? string.Empty,
            PlanView.Text(report["error"]),
            IsStopped: status is ReportStatus.Drafting or ReportStatus.Saving && !view.IsBusy);
    }
}

/// <summary>One line of the agent's "What Beacon checked" activity.</summary>
public sealed record CheckView(string Label, string Status, string? Finding);

/// <summary>An activity message of type <c>beacon.checks</c>, projected from its content.</summary>
public sealed record ChecksView(string MessageId, string Title, IReadOnlyList<CheckView> Checks)
{
    public const string ActivityType = "beacon.checks";

    public bool Equals(ChecksView? other) =>
        other is not null && MessageId == other.MessageId && Title == other.Title && Checks.SequenceEqual(other.Checks);

    public override int GetHashCode() => HashCode.Combine(MessageId, Checks.Count);

    public static ChecksView? From(ActivityMessageView activity)
    {
        if (activity.ActivityType != ActivityType)
        {
            return null; // the set of activity types is the producer's; skip the ones we don't render
        }

        List<CheckView> checks = [];
        if (activity.Content["checks"] is JsonArray lines)
        {
            foreach (var line in lines.OfType<JsonObject>())
            {
                checks.Add(new CheckView(
                    PlanView.Text(line["label"]) ?? "(check)",
                    PlanView.Text(line["status"]) ?? "running",
                    PlanView.Text(line["finding"])));
            }
        }

        return new ChecksView(activity.Id, PlanView.Text(activity.Content["title"]) ?? "Checks", checks);
    }

    /// <summary>One sentence for the transcript and screen readers.</summary>
    public string Describe() =>
        $"{Title}: " + string.Join("; ", Checks.Select(c => c.Finding is null ? $"{c.Label}, {c.Status}" : $"{c.Label}, {c.Finding}")) + ".";
}

/// <summary>What the status line shows: the run's current step, if the agent marks its phases.</summary>
public sealed record PhaseView(string? StepName, bool IsBusy)
{
    public string Label => StepName switch
    {
        null when IsBusy => "Beacon is working…",
        null => "Ready",
        "planning" => "Planning the diagnosis…",
        "investigating" => "Investigating…",
        "reporting" => "Drafting the service report…",
        var name => $"{name}…", // step names are the producer's; show unknown ones as they are
    };

    /// <remarks>
    /// Between one step's <c>STEP_FINISHED</c> and the next one's <c>STEP_STARTED</c> no step is active.
    /// Showing "working" for that instant would make the line flicker, so the last step of the run stays
    /// up until another replaces it or the run ends.
    /// </remarks>
    public static PhaseView From(ConversationView view)
    {
        if (!view.IsBusy)
        {
            return new(null, IsBusy: false);
        }

        var run = view.CurrentRun?.RunId;
        var step = view.Steps.LastOrDefault(s => s.IsActive && s.RunId == run) ?? view.Steps.LastOrDefault(s => s.RunId == run);
        return new(step?.Name, IsBusy: true);
    }
}

/// <summary>
/// Everything the progress panels show, at one moment. Tests compare boards to prove that a recorded
/// run always produces the same visible progression.
/// </summary>
public sealed record ProgressBoard(PhaseView Phase, PlanView? Plan, ReportView? Report, ChecksView? Checks)
{
    public static ProgressBoard From(ConversationView view) => new(
        PhaseView.From(view),
        PlanView.From(view),
        ReportView.From(view),
        view.Messages.OfType<ActivityMessageView>().Select(ChecksView.From).LastOrDefault(c => c is not null));

    /// <summary>A compact, stable text form of the board: one frame of the progression.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append("step: ").Append(Phase.StepName ?? "-");
        text.Append(" | plan: ").Append(Plan is null ? "-" : string.Join(' ', Plan.Steps.Select(s => s.Status switch
        {
            PlanStepStatus.Done => "x",
            PlanStepStatus.InProgress => ">",
            _ => ".",
        })));
        text.Append(" | report: ").Append(Report is null ? "-"
            : $"{Report.Status.ToString().ToLowerInvariant()} {Report.Summary.Length}/{Report.Findings.Length}/{Report.Recommendation.Length}");
        text.Append(" | checks: ").Append(Checks is null ? "-" : string.Join(' ', Checks.Checks.Select(c => c.Status)));
        return text.ToString();
    }
}
