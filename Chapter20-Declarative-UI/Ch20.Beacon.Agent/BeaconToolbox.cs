using System.ComponentModel;
using System.Text.Json;
using Beacon.AI;
using Beacon.Domain;
using Ch20.Beacon.Agent.Inspections;
using Ch20.Beacon.Agent.Surfaces;
using Ch20.DeclarativeUi;
using Microsoft.Extensions.AI;

namespace Ch20.Beacon.Agent;

/// <summary>Everything Beacon offers the model in Chapter 20: its instructions, its read-only tools, the templates and render_a2ui.</summary>
public sealed class BeaconToolbox
{
    /// <summary>Server-owned instructions for forms. The catalog travels in the prompt, as A2UI v0.9 intends.</summary>
    public static readonly string Instructions = BeaconInstructions.Default +
        $"""

        - When the dispatcher asks for an inspection or a checklist, call {InspectionTemplates.ToolName} for the work
          order, then describe the form with {A2ui.RenderToolName}: a Card whose child is a Column with a title;
          a template over /checks repeating one CheckBox whose label binds the relative path label and whose value binds passed;
          one input per reading bound to /readings/<id> and labeled with the reading's label and unit, as in
          "Suction pressure (psig)"; a ChoicePicker labeled "{InspectionTemplate.ConditionLabel}" bound to /condition,
          with the template's conditions as options; a TextField bound to /notes; and one Button whose action is
          {InspectionRules.SubmitAction}, with context workOrderId plus bindings to /checks, /readings,
          /condition and /notes. Put the work order, the checks (passed: false), empty readings (a scale reading
          at its minimum), an empty condition list and empty notes in data. Use only this catalog; Beacon sets
          the catalog ID itself.
        - If {A2ui.RenderToolName} rejects the form, fix exactly what it lists and call it again, or answer in text.
        - A submitted form reaches you as a {A2ui.LogEventToolName} result. Summarize what was recorded in a
          sentence or two; don't ask for the values again.

        {BeaconCatalog.FieldForms.Describe()}
        """;

    private static readonly JsonSerializerOptions Compact = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    private readonly HashSet<string> _serverToolNames;

    public BeaconToolbox(IFieldServiceStore store, InspectionTemplates templates, RenderSurfaceTool render)
    {
        Tools =
        [
            .. new BeaconTools(store).CreateReadOnlyTools(),
            AIFunctionFactory.Create(
                ([Description("The work order, for example WO-1003.")] string workOrderId) => templates.Describe(workOrderId),
                new AIFunctionFactoryOptions
                {
                    Name = InspectionTemplates.ToolName,
                    Description = "The manufacturer's inspection for a work order's equipment: its checks, readings and overall conditions.",
                    SerializerOptions = Compact,
                }),
            render,
        ];

        _serverToolNames = [.. Tools.Select(t => t.Name)];
    }

    public IReadOnlyList<AITool> Tools { get; }

    /// <summary>Sets the server's instructions and tools. A client tool can't take a server tool's name.</summary>
    public void Configure(ChatOptions options)
    {
        options.Instructions = Instructions;
        options.Tools = [.. Tools, .. (options.Tools ?? []).Where(t => !_serverToolNames.Contains(t.Name))];
    }
}
