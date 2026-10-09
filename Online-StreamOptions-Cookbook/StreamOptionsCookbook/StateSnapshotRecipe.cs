using System.ComponentModel;
using System.Text.Json;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook;

/// <summary>One line of a work order's checklist on the shared sheet.</summary>
public sealed record ChecklistEntry(string Id, string Text, bool Done);

/// <summary>The shared work-order sheet: the state document the agent and the Desk both edit.</summary>
public sealed record WorkSheet(string WorkOrderId, string Problem, IReadOnlyList<ChecklistEntry> Checklist);

/// <summary>Recipe: <see cref="AGUIStreamOptions.MapResultAsStateSnapshot"/> makes a tool's result the new state.</summary>
public static class StateSnapshotRecipe
{
    public const string OpenWorkOrderSheetName = "open_work_order_sheet";

    public static AIFunction OpenWorkOrderSheet { get; } = AIFunctionFactory.Create(
        ([Description("The work order to open, for example WO-1001.")] string workOrderId) => Open(workOrderId),
        OpenWorkOrderSheetName,
        "Opens a work order on the shared sheet and returns the whole sheet.",
        CookbookJson.ToolOptions);

    public static AGUIStreamOptions Apply(AGUIStreamOptions options) => options
        .MapResultAsStateSnapshot(OpenWorkOrderSheetName);

    // The result IS the new state, so there is no room for an error message in it. A work order the
    // tool can't open leaves the sheet as the client sent it.
    private static JsonElement Open(string workOrderId) => workOrderId switch
    {
        "WO-1001" => JsonSerializer.SerializeToElement(Wo1001, CookbookJsonContext.Default.WorkSheet),
        _ => CookbookJson.CurrentRunState(),
    };

    private static readonly WorkSheet Wo1001 = new(
        "WO-1001",
        "Air conditioner short-cycling and blowing warm air",
        [
            new("C1", "Check thermostat settings and wiring", false),
            new("C2", "Measure refrigerant pressures", false),
            new("C3", "Inspect run capacitor", false),
            new("C4", "Test compressor amperage", false),
        ]);
}
