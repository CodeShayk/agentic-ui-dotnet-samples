using AGUI.Server;

namespace Ch13.Beacon.Agent;

/// <summary>How Chapter 13's agent turns tool calls and results into AG-UI events, for one run.</summary>
public static class BeaconStreamOptions
{
    public static AGUIStreamOptions Create(PredictiveStateWriter state) =>
        new AGUIStreamOptions()
            // Each argument fragment as its own TOOL_CALL_ARGS (Chapter 9).
            .MapStreamingToolCallArguments(ToolArgumentFragments.Extract)
            // Complete calls whose arguments are state: emitted after TOOL_CALL_END, before the tool runs.
            .MapCall(DiagnosticTools.SetPlanName, state.PlanSet)
            .MapCall(DiagnosticTools.UpdatePlanStepName, state.PlanStepUpdated)
            .MapCall(DiagnosticTools.WriteReportName, state.ReportCompleted)
            // The tool's verdict on the prediction: saved, or rejected with the reason.
            .MapResult(DiagnosticTools.WriteReportName, state.ReportSaved);
}
