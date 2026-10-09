using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.Domain;

namespace Ch20.Beacon.Agent.Inspections;

/// <summary>A pass/fail check on an inspection.</summary>
public sealed record InspectionCheck(string Id, string Label);

/// <summary>How a reading is entered: a measured value typed in, or a position on a short scale.</summary>
public enum ReadingKind { Measured, Scale }

/// <summary>A reading the technician takes, with its unit and plausible range.</summary>
public sealed record InspectionReading(string Id, string Label, string Unit, double Min, double Max, ReadingKind Kind = ReadingKind.Measured)
{
    /// <summary>The label a form must show on this reading's input: the template's words and unit, never the model's.</summary>
    public string InputLabel => $"{Label} ({Unit})";
}

/// <summary>An answer to "overall condition".</summary>
public sealed record InspectionOption(string Value, string Label);

/// <summary>
/// The manufacturer's inspection for one equipment model. These change with every service bulletin, which
/// is the point of the chapter: the form is data on the server, not a component in a client release.
/// </summary>
public sealed record InspectionTemplate(
    string Model,
    string Title,
    string Source,
    IReadOnlyList<InspectionCheck> Checks,
    IReadOnlyList<InspectionReading> Readings)
{
    /// <summary>The label a form must show on the overall-condition picker.</summary>
    public const string ConditionLabel = "Overall condition";

    public static IReadOnlyList<InspectionOption> Conditions { get; } =
    [
        new("pass", "Pass"),
        new("follow_up", "Needs follow-up"),
        new("fail", "Fail"),
    ];
}

/// <summary>The templates Beacon knows, by equipment model. In production this is a table the service team edits.</summary>
public sealed class InspectionTemplates(IFieldServiceStore store)
{
    public const string ToolName = "get_inspection_template";

    private static readonly Dictionary<string, InspectionTemplate> ByModel = new(StringComparer.Ordinal)
    {
        ["CA-36X"] = new("CA-36X", "CA-36X cooling inspection", "Contoso Air service manual CA-36X, section 7 (rev. 2026-08)",
        [
            new("contactor", "Contactor pulls in on a call for cooling"),
            new("capacitor", "Run capacitor within 6% of its rating"),
            new("condenser_coil", "Condenser coil clean and undamaged"),
            new("line_insulation", "Suction line insulation intact"),
            new("condensate", "Condensate drain clear"),
        ],
        [
            new("capacitor_uf", "Capacitor reading, compressor side", "uF", 0, 100),
            new("suction_psig", "Suction pressure", "psig", 0, 400),
            new("supply_air_f", "Supply air temperature", "F", 30, 120),
        ]),

        ["HP-24S"] = new("HP-24S", "HP-24S heat pump inspection", "Contoso Air service bulletin SB-HP24S-09 (October 2026)",
        [
            new("fan_bearings", "Outdoor fan turns freely, with no bearing noise"),
            new("fan_blade", "Fan blade secure and undamaged"),
            new("defrost_sensor", "Defrost sensor clipped tight to the coil"),
            new("reversing_valve", "Reversing valve shifts on demand"),
            new("outdoor_coil", "Outdoor coil clean"),
        ],
        [
            new("fan_amps", "Outdoor fan running current", "A", 0, 10),
            new("defrost_kohm", "Defrost sensor resistance", "kOhm", 0, 100),
            new("suction_psig", "Suction pressure", "psig", 0, 400),
        ]),

        ["RT-120"] = new("RT-120", "RT-120 rooftop unit inspection", "Contoso Air RT-120 commissioning checklist (rev. 2026-05)",
        [
            new("contactor_wiring", "Compressor contactor and wiring intact"),
            new("economizer", "Economizer damper moves freely"),
            new("belt", "Blower belt tension correct"),
            new("filters", "Filters clean"),
        ],
        [
            new("compressor_amps", "Compressor current, L1", "A", 0, 60),
            new("discharge_air_f", "Discharge air temperature", "F", 30, 140),
            new("vibration", "Compressor vibration", "0 none to 5 severe", 0, 5, ReadingKind.Scale),
        ]),
    };

    /// <summary>The template for a work order's equipment, with the work order it's for.</summary>
    public (WorkOrder WorkOrder, Equipment Equipment, Customer Customer, InspectionTemplate Template)? For(string workOrderId) =>
        store.GetWorkOrder(workOrderId) is { } workOrder
        && store.GetEquipment(workOrder.EquipmentId) is { } equipment
        && store.GetCustomer(workOrder.CustomerId) is { } customer
        && ByModel.TryGetValue(equipment.Model, out var template)
            ? (workOrder, equipment, customer, template)
            : null;

    /// <summary><c>get_inspection_template</c>: what the model reads before it describes a form.</summary>
    public JsonElement Describe(string workOrderId)
    {
        if (For(workOrderId) is not { } found)
        {
            return ToElement(new JsonObject { ["error"] = $"There is no inspection template for {workOrderId}'s equipment." });
        }

        var (workOrder, equipment, customer, template) = found;

        return ToElement(new JsonObject
        {
            ["workOrderId"] = workOrder.Id,
            ["equipmentId"] = equipment.Id,
            ["customer"] = customer.Name,
            ["model"] = template.Model,
            ["title"] = template.Title,
            ["source"] = template.Source,
            ["checks"] = new JsonArray([.. template.Checks.Select(c => new JsonObject { ["id"] = c.Id, ["label"] = c.Label })]),
            ["readings"] = new JsonArray([.. template.Readings.Select(r => new JsonObject
            {
                ["id"] = r.Id,
                ["label"] = r.Label,
                ["unit"] = r.Unit,
                ["min"] = r.Min,
                ["max"] = r.Max,
                ["kind"] = r.Kind == ReadingKind.Scale ? "scale" : "measured",
            })]),
            ["conditions"] = new JsonArray([.. InspectionTemplate.Conditions.Select(c => new JsonObject { ["value"] = c.Value, ["label"] = c.Label })]),
        });
    }

    internal static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
