using AGUI.Abstractions;
using Ch13.Beacon.Desk.Progress;
using Ch13.Tests.Infrastructure;

namespace Ch13.Tests;

/// <summary>
/// The captured diagnosis (Recordings/diagnose-wo-1001.sse) replayed through Beacon Desk's real client
/// stack. A frame is what the progress panels show after one change, written compactly by
/// <see cref="ProgressBoard.Describe"/>; consecutive identical frames are one frame.
/// </summary>
public sealed class ReplayTests
{
    [Fact]
    public async Task The_recorded_run_replays_the_same_visible_progression_every_time()
    {
        var golden = Path.Combine(AppContext.BaseDirectory, "Recordings", "diagnose-wo-1001.frames.txt");
        var expected = File.Exists(golden) ? await File.ReadAllLinesAsync(golden, TestContext.Current.CancellationToken) : [];

        for (var replay = 0; replay < 3; replay++)
        {
            using var harness = await Harness.ReplayAsync();
            var frames = harness.Frames();
            if (!expected.SequenceEqual(frames))
            {
                // Review the difference, then copy this file over the golden one if the change is intended.
                await File.WriteAllLinesAsync(golden + ".actual", frames, TestContext.Current.CancellationToken);
            }

            Assert.Equal(expected, frames);
        }
    }

    [Fact]
    public async Task The_progression_only_moves_forward()
    {
        using var harness = await Harness.ReplayAsync();
        var boards = harness.Published.Select(p => ProgressBoard.From(p.View)).ToList();

        // A plan step never goes back to an earlier status.
        var plans = boards.Select(b => b.Plan).OfType<PlanView>().ToList();
        Assert.All(plans.Zip(plans.Skip(1)), pair =>
            Assert.All(pair.First.Steps.Zip(pair.Second.Steps), step => Assert.True(step.Second.Status >= step.First.Status)));
        Assert.All(plans[^1].Steps, step => Assert.Equal(PlanStepStatus.Done, step.Status));

        // While Beacon drafts, the report only grows; then it is saving, then saved.
        var reports = boards.Select(b => b.Report).OfType<ReportView>().ToList();
        Assert.All(reports.Zip(reports.Skip(1)), pair =>
        {
            Assert.True(pair.Second.Summary.Length >= pair.First.Summary.Length);
            Assert.True(pair.Second.Findings.Length >= pair.First.Findings.Length);
            Assert.True(pair.Second.Recommendation.Length >= pair.First.Recommendation.Length);
            Assert.True(pair.Second.Status >= pair.First.Status);
        });
        Assert.Equal(
            [ReportStatus.Drafting, ReportStatus.Saving, ReportStatus.Saved],
            Collapse(reports.Select(r => r.Status)));
        Assert.True(reports.Count(r => r.Status == ReportStatus.Drafting) > 40, "the report should draft itself over many frames");

        // The status line follows the agent's steps, in order.
        Assert.Equal(
            [null, "planning", "investigating", "reporting", null],
            Collapse(boards.Select(b => b.Phase.StepName)));
    }

    [Fact]
    public async Task While_a_lookup_runs_the_screen_shows_what_the_agent_predicted()
    {
        // The moment the agent's tools start working on the fault history: everything sent before the
        // first result of that turn has been processed. What does Dana see?
        var aligned = await WhileToolsRunAsync(aligned: true, "get_fault_history");
        var buffered = await WhileToolsRunAsync(aligned: false, "get_fault_history");

        Assert.Equal("step: investigating | plan: x > . . | report: - | checks: done running", aligned);

        // Without the aligned transport, AGUIChatClient holds everything after the first TOOL_CALL_END
        // until the turn's results arrive: the plan still shows the previous step, the check is missing.
        Assert.Equal("step: investigating | plan: > . . . | report: - | checks: done", buffered);
    }

    [Fact]
    public async Task While_the_report_is_saved_the_screen_says_so()
    {
        var aligned = await WhileToolsRunAsync(aligned: true, "write_service_report");
        var buffered = await WhileToolsRunAsync(aligned: false, "write_service_report");

        Assert.Contains("report: saving", aligned, StringComparison.Ordinal);
        Assert.Contains("report: drafting", buffered, StringComparison.Ordinal); // the MapCall delta is held back
    }

    // Runs of equal values become one value: what a screen showing each in turn would look like.
    private static List<T> Collapse<T>(IEnumerable<T> values)
    {
        List<T> collapsed = [];
        foreach (var value in values)
        {
            if (collapsed.Count == 0 || !EqualityComparer<T>.Default.Equals(collapsed[^1], value))
            {
                collapsed.Add(value);
            }
        }

        return collapsed;
    }

    private static async Task<string> WhileToolsRunAsync(bool aligned, string tool)
    {
        string? frame = null;
        string? callId = null;
        var ended = false;

        using var harness = await Harness.ReplayAsync(aligned, before: h => h.Transport.BeforeEach = evt =>
        {
            switch (evt)
            {
                case ToolCallStartEvent start when start.ToolCallName == tool:
                    callId = start.ToolCallId;
                    break;
                case ToolCallEndEvent end when end.ToolCallId == callId:
                    ended = true;
                    break;
                case ToolCallResultEvent when ended && frame is null:
                    frame = ProgressBoard.From(h.Conversation.View).Describe();
                    break;
            }
        });

        return frame ?? throw new InvalidOperationException($"The recording has no result after {tool}.");
    }
}
