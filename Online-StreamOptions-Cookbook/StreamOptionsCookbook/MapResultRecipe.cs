using System.ComponentModel;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook;

/// <summary>A unit's warranty, as the warranty tool reports it.</summary>
public sealed record WarrantyCheck(string EquipmentId, bool Covered, DateOnly? Expires, string Terms);

/// <summary>Recipe: <see cref="AGUIStreamOptions.MapResult"/> lets a tool's result drive more of the UI than its card.</summary>
public static class MapResultRecipe
{
    public const string CheckWarrantyName = "check_warranty";

    /// <summary>The <c>CUSTOM</c> event the Desk listens for to update its warranty badge.</summary>
    public const string WarrantyCheckedEvent = "beacon.warranty_checked";

    public static AIFunction CheckWarranty { get; } = AIFunctionFactory.Create(
        ([Description("The equipment ID, for example EQ-501.")] string equipmentId) => Lookup(equipmentId),
        CheckWarrantyName,
        "Checks whether a unit is under warranty.",
        CookbookJson.ToolOptions);

    public static AGUIStreamOptions Apply(AGUIStreamOptions options) => options
        .MapResult(CheckWarrantyName, result =>
            result is { Exception: null, Result: JsonElement { ValueKind: JsonValueKind.Object } warranty }
                ? [new CustomEvent { Name = WarrantyCheckedEvent, Value = warranty }]
                : []); // a failed call: TOOL_CALL_RESULT already says so, and the badge keeps its last value

    private static WarrantyCheck Lookup(string equipmentId) => equipmentId switch
    {
        "EQ-501" => new(equipmentId, true, new DateOnly(2029, 5, 14), "Parts: 10 years; labor: 2 years (labor expired)"),
        _ => new(equipmentId, false, null, "No warranty on record."),
    };
}
