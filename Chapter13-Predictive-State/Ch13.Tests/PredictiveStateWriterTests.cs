using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AI;
using Ch13.Beacon.Agent;
using Microsoft.Extensions.AI;

namespace Ch13.Tests;

/// <summary>The per-run state writer on its own: what it predicts, and what it refuses to send twice.</summary>
public sealed class PredictiveStateWriterTests
{
    private static readonly Dictionary<string, object?> Report = new()
    {
        ["workOrderId"] = "WO-1001",
        ["summary"] = "The unit short-cycles.",
        ["findings"] = "E5 on 2026-11-14.",
        ["recommendation"] = "Test the run capacitor.",
    };

    [Fact]
    public void Pieces_that_change_nothing_a_reader_sees_send_nothing()
    {
        var writer = new PredictiveStateWriter();
        var arguments = JsonSerializer.Serialize(Report, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Dictionary<string, object?>)));

        // Twelve characters at a time, as the scripted model streams them.
        var pieces = Enumerable.Range(0, (arguments.Length + 11) / 12)
            .Select(i => arguments.Substring(i * 12, Math.Min(12, arguments.Length - (i * 12))))
            .ToList();
        var deltas = pieces.Select((piece, i) => writer.ReportFragment(Fragment(i == 0, piece))).ToList();

        Assert.Equal(pieces.Count, deltas.Count);
        Assert.Contains(deltas, d => d is null); // a property name, a quote, a comma
        Assert.Equal("""[{"op":"add","path":"/report","value":{"status":"drafting"}}]""", Text(deltas.First(d => d is not null)!));
        Assert.All(deltas.OfType<StateDeltaEvent>().Skip(1), d =>
            Assert.All(d.Delta.EnumerateArray(), op => Assert.StartsWith("/report/", op.GetProperty("path").GetString(), StringComparison.Ordinal)));
    }

    [Fact]
    public void A_half_written_identifier_is_never_predicted()
    {
        var writer = new PredictiveStateWriter();

        Assert.Equal("""[{"op":"add","path":"/report","value":{"status":"drafting"}}]""", Text(writer.ReportFragment(Fragment(true, """{"workOrderId":"WO-10"""))!));
        Assert.Null(writer.ReportFragment(Fragment(false, "0")));
        Assert.Equal("""[{"op":"add","path":"/report/workOrderId","value":"WO-1001"}]""", Text(writer.ReportFragment(Fragment(false, "1\""))!));
    }

    [Fact]
    public void After_streaming_MapCall_sends_only_what_changed()
    {
        var writer = new PredictiveStateWriter();
        var arguments = JsonSerializer.Serialize(Report, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Dictionary<string, object?>)));
        writer.ReportFragment(Fragment(true, arguments));

        var completed = Assert.Single(writer.ReportCompleted(Call(DiagnosticTools.WriteReportName, Report)));

        Assert.Equal("""[{"op":"add","path":"/report/status","value":"saving"}]""", Text(completed));
    }

    [Fact]
    public void Without_streaming_MapCall_sends_the_whole_report_once()
    {
        var writer = new PredictiveStateWriter();

        var first = Assert.Single(writer.ReportCompleted(Call(DiagnosticTools.WriteReportName, Report)));
        var again = writer.ReportCompleted(Call(DiagnosticTools.WriteReportName, Report, "call-2"));

        using var delta = JsonDocument.Parse(Text(first));
        var operation = Assert.Single(delta.RootElement.EnumerateArray());
        Assert.Equal(("add", "/report"), (operation.GetProperty("op").GetString(), operation.GetProperty("path").GetString()));
        Assert.Equal("saving", operation.GetProperty("value").GetProperty("status").GetString());
        Assert.Empty(again); // the model sent the same report twice; the client already has it
    }

    [Fact]
    public void The_result_confirms_or_corrects_the_prediction()
    {
        var saved = new PredictiveStateWriter();
        saved.ReportCompleted(Call(DiagnosticTools.WriteReportName, Report)).ToList();
        var rejected = new PredictiveStateWriter();
        rejected.ReportCompleted(Call(DiagnosticTools.WriteReportName, Report)).ToList();

        var ok = Assert.Single(saved.ReportSaved(new FunctionResultContent("call-1", new ReportReceipt("WO-1001", 12))));
        var refused = Assert.Single(rejected.ReportSaved(new FunctionResultContent("call-1", new ToolError("Work order WO-1001 is closed."))));

        Assert.Equal("""[{"op":"add","path":"/report/status","value":"saved"}]""", Text(ok));
        Assert.Equal(
            """[{"op":"add","path":"/report/status","value":"rejected"},{"op":"add","path":"/report/error","value":"Work order WO-1001 is closed."}]""",
            Text(refused));
    }

    [Fact]
    public void Plan_updates_are_validated_and_deduplicated()
    {
        var writer = new PredictiveStateWriter();
        var plan = Call(DiagnosticTools.SetPlanName, new() { ["workOrderId"] = "WO-1001", ["steps"] = new[] { "Review", "Report" } });

        Assert.Single(writer.PlanSet(plan));
        Assert.Empty(writer.PlanSet(plan)); // the same plan again

        Assert.Equal(
            """[{"op":"replace","path":"/plan/steps/0/status","value":"done"},{"op":"add","path":"/plan/steps/0/note","value":"Scheduled"}]""",
            Text(Assert.Single(writer.PlanStepUpdated(Step(0, "done", "Scheduled")))));
        Assert.Empty(writer.PlanStepUpdated(Step(0, "done", "Scheduled")));   // nothing changed
        Assert.Empty(writer.PlanStepUpdated(Step(7, "done")));                // no such step
        Assert.Empty(writer.PlanStepUpdated(Step(1, "finished")));            // no such status
        Assert.True(writer.HasPlanStep(1));
        Assert.False(writer.HasPlanStep(2));
    }

    [Fact]
    public void The_baseline_is_the_client_state_and_only_Beacons_fields_are_read_from_it()
    {
        using var state = JsonDocument.Parse("""
            {
              "report": { "workOrderId": "WO-1001", "status": "saved", "summary": "The unit short-cycles.",
                          "findings": "E5 on 2026-11-14.", "recommendation": "Old advice.", "injected": "<script>" },
              "plan": { "steps": "not a list" }
            }
            """);
        var writer = new PredictiveStateWriter(state.RootElement);

        // A redraft that changes one field patches that field and the status, nothing else.
        var redraft = new Dictionary<string, object?>(Report) { ["recommendation"] = "Test the run capacitor." };
        var delta = Assert.Single(writer.ReportCompleted(Call(DiagnosticTools.WriteReportName, redraft)));

        Assert.Equal(
            """[{"op":"add","path":"/report/status","value":"saving"},{"op":"add","path":"/report/recommendation","value":"Test the run capacitor."}]""",
            Text(delta));
        Assert.False(writer.HasPlanStep(0)); // a malformed plan is no plan
    }

    private static AGUIToolCallArgumentFragment Fragment(bool first, string delta) => new()
    {
        Index = 0,
        ToolCallId = first ? "call-1" : null,
        FunctionName = first ? DiagnosticTools.WriteReportName : null,
        ArgumentsDelta = delta,
    };

    private static FunctionCallContent Call(string name, Dictionary<string, object?> arguments, string callId = "call-1") =>
        new(callId, name, arguments);

    private static FunctionCallContent Step(int index, string status, string? note = null)
    {
        Dictionary<string, object?> arguments = new() { ["index"] = index, ["status"] = status };
        if (note is not null)
        {
            arguments["note"] = note;
        }

        return Call(DiagnosticTools.UpdatePlanStepName, arguments);
    }

    private static string Text(BaseEvent evt) => evt switch
    {
        StateDeltaEvent delta => delta.Delta.GetRawText(),
        _ => throw new InvalidOperationException($"Expected a STATE_DELTA, got {evt.Type}."),
    };
}
