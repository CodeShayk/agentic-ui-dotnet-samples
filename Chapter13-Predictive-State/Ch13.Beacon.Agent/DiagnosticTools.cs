using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.AI;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch13.Beacon.Agent;

/// <summary>What <c>set_diagnostic_plan</c> tells the model.</summary>
public sealed record PlanReceipt(string WorkOrderId, int Steps);

/// <summary>What <c>update_plan_step</c> tells the model.</summary>
public sealed record PlanStepReceipt(int Index, string Status);

/// <summary>What <c>write_service_report</c> tells the model once the report is saved.</summary>
public sealed record ReportReceipt(string WorkOrderId, int Words);

/// <summary>A service report as Beacon saves it against a work order.</summary>
public sealed record ServiceReport(string WorkOrderId, string Summary, string Findings, string Recommendation);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PlanReceipt))]
[JsonSerializable(typeof(PlanStepReceipt))]
[JsonSerializable(typeof(ReportReceipt))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class DiagnosticJsonContext : JsonSerializerContext;

/// <summary>The rules a diagnostic plan follows, checked by the tools and by the state writer alike.</summary>
public static class DiagnosticPlan
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Done = "done";

    public const int MaxSteps = 8;
    public const int MaxTitleLength = 80;
    public const int MaxNoteLength = 160;

    public static bool IsStatus(string? status) => status is Pending or InProgress or Done;

    /// <summary>Returns why <paramref name="steps"/> isn't an acceptable plan, or <see langword="null"/> when it is.</summary>
    public static string? Problem(IReadOnlyList<string> steps) =>
        steps.Count is 0 or > MaxSteps ? $"A plan needs between 1 and {MaxSteps} steps."
        : steps.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > MaxTitleLength) ? $"Each step needs a title of at most {MaxTitleLength} characters."
        : null;
}

/// <summary>
/// The tools behind Chapter 13's diagnosis. Two of them exist to change what Dana sees: the model
/// writes its plan with <c>set_diagnostic_plan</c> and checks it off with <c>update_plan_step</c>, and
/// <see cref="PredictiveStateWriter"/> turns each call's arguments into state. The third,
/// <c>write_service_report</c>, does real work: it validates the report and saves it to the work order.
/// </summary>
public sealed class DiagnosticTools(IFieldServiceStore store, ServiceReportArchive archive)
{
    public const string SetPlanName = "set_diagnostic_plan";
    public const string UpdatePlanStepName = "update_plan_step";
    public const string WriteReportName = "write_service_report";

    public const int MaxReportFieldLength = 2000;

    /// <summary>Serializer options for these tools: their own types first, then Beacon's.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    /// <summary>Creates the tools. <paramref name="saveDelay"/> stands in for the work-order system's latency.</summary>
    public IList<AITool> Create(TimeSpan saveDelay)
    {
        AIFunction writeReport = AIFunctionFactory.Create(WriteServiceReport, WriteReportName,
            "Saves the service report for a work order. Write plain sentences, no Markdown.", JsonOptions);

        return
        [
            AIFunctionFactory.Create(SetDiagnosticPlan, SetPlanName,
                "Shows the dispatcher your plan for diagnosing a work order, as short steps in order. Call it before you start.", JsonOptions),
            AIFunctionFactory.Create(UpdatePlanStep, UpdatePlanStepName,
                "Marks a step of your plan in_progress before you start it, or done when it's finished, with a one-line note.", JsonOptions),
            saveDelay > TimeSpan.Zero ? new DelayedTool(writeReport, saveDelay) : writeReport,
        ];
    }

    public object SetDiagnosticPlan(
        [Description("The work order being diagnosed, for example WO-1001.")] string workOrderId,
        [Description("The steps, in order, each a short imperative sentence.")] string[] steps)
    {
        if (store.GetWorkOrder(workOrderId) is null)
        {
            return new ToolError($"Work order {workOrderId} was not found.");
        }

        return DiagnosticPlan.Problem(steps) is { } problem
            ? new ToolError(problem)
            : new PlanReceipt(workOrderId, steps.Length);
    }

    public object UpdatePlanStep(
        [Description("The zero-based index of the step.")] int index,
        [Description("pending, in_progress or done.")] string status,
        [Description("Optional: one line on what you found, when the step is done.")] string? note = null)
    {
        // The plan this run showed Dana, if any: the same writer that turned the call into state.
        var writer = PredictiveStateWriter.From(FunctionInvokingChatClient.CurrentContext?.Options);
        var problem = !DiagnosticPlan.IsStatus(status) ? "Status must be pending, in_progress or done."
            : note?.Length > DiagnosticPlan.MaxNoteLength ? $"Keep the note under {DiagnosticPlan.MaxNoteLength} characters."
            : writer is not null && !writer.HasPlanStep(index) ? $"The plan has no step {index}."
            : null;

        return problem is null ? new PlanStepReceipt(index, status) : new ToolError(problem);
    }

    public object WriteServiceReport(
        [Description("The work order the report is for.")] string workOrderId,
        [Description("One or two sentences: the equipment and the symptom.")] string summary,
        [Description("What the investigation found, citing fault codes and dates.")] string findings,
        [Description("What the technician should do next, with part numbers and prices.")] string recommendation)
    {
        if (store.GetWorkOrder(workOrderId) is null)
        {
            return new ToolError($"Work order {workOrderId} was not found.");
        }

        string[] fields = [summary, findings, recommendation];
        if (fields.Any(f => string.IsNullOrWhiteSpace(f) || f.Length > MaxReportFieldLength))
        {
            return new ToolError($"Each part of the report needs between 1 and {MaxReportFieldLength} characters.");
        }

        archive.Save(new ServiceReport(workOrderId, summary, findings, recommendation));
        var words = fields.Sum(f => f.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        return new ReportReceipt(workOrderId, words);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(BeaconJson.Options);
        options.TypeInfoResolverChain.Insert(0, DiagnosticJsonContext.Default);
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>Saved service reports, by work order. In memory, like the rest of Beacon's back office.</summary>
public sealed class ServiceReportArchive
{
    private readonly ConcurrentDictionary<string, ServiceReport> _reports = new(StringComparer.OrdinalIgnoreCase);

    public void Save(ServiceReport report) => _reports[report.WorkOrderId] = report;

    public ServiceReport? Find(string workOrderId) => _reports.TryGetValue(workOrderId, out var report) ? report : null;
}
