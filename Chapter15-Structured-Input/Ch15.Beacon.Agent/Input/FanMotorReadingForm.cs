using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.AI;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch15.Beacon.Agent.Input;

/// <summary>
/// Asks the technician to measure an outdoor fan motor: its running current, what it sounds like and
/// whether the shaft has play. Nothing in Beacon's records can answer this; it exists only on site.
/// </summary>
public sealed class FanMotorReadingForm(IFieldServiceStore store) : InputForm
{
    public const string FormId = "fan-motor-reading";
    public const string Name = "ask_for_fan_motor_reading";

    // Nameplate full-load amps of the outdoor fan motors Contoso Climate services.
    private static readonly Dictionary<string, decimal> RatedAmps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HP-24S"] = 1.4m,
        ["CA-36X"] = 1.9m,
    };

    public override string Id => FormId;

    public override string ToolName => Name;

    public override AIFunction CreateTool() => AIFunctionFactory.Create(
        OnNoQuestion,
        Name,
        "Asks the technician on site to measure the outdoor fan motor (running current, noise and shaft play) before you " +
        "choose between replacing the motor and replacing its bearings. The run pauses until they answer.",
        Ch15Json.Options);

    // Runs only when there was nothing to ask: no such unit, or a unit without an outdoor fan.
    private object OnNoQuestion([Description("The equipment ID, for example EQ-601.")] string equipmentId) =>
        store.GetEquipment(equipmentId) is { } equipment
            ? new ToolError($"{equipment.Id} is a {equipment.Kind}; it has no outdoor fan motor to measure.")
            : new ToolError($"Equipment {equipmentId} was not found.");

    public override InputQuestion? Ask(IDictionary<string, object?>? arguments)
    {
        if (store.GetEquipment(Argument(arguments, "equipmentId")) is not { Kind: EquipmentKind.HeatPump or EquipmentKind.AirConditioner } equipment)
        {
            return null;
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["title"] = $"Outdoor fan motor on {equipment.Id}",
            ["properties"] = new JsonObject
            {
                ["amps"] = new JsonObject
                {
                    ["type"] = "number",
                    ["title"] = "Running current (A)",
                    ["description"] = "Clamp meter on the motor's common lead, with the fan running.",
                    ["minimum"] = 0,
                    ["maximum"] = 10,
                },
                ["noise"] = new JsonObject
                {
                    ["type"] = "string",
                    ["title"] = "What does it sound like?",
                    ["enum"] = new JsonArray("grinding", "squealing", "rattling", "quiet"),
                },
                ["shaftPlay"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["title"] = "The shaft wobbles when I move it by hand",
                    ["default"] = false,
                },
            },
            ["required"] = new JsonArray("amps", "noise"),
            ["additionalProperties"] = false,
        };

        // Metadata is context for the person, not part of the answer: the rating to compare the reading with.
        var metadata = new JsonObject { ["form"] = FormId, ["equipmentId"] = equipment.Id, ["model"] = equipment.Model };
        if (RatedAmps.TryGetValue(equipment.Model, out var rated))
        {
            metadata["ratedAmps"] = rated;
        }

        return Question(
            equipment.Id,
            $"Measure the outdoor fan motor on {equipment.Id} with the fan running. Beacon uses the reading to choose " +
            "between a new motor and a bearing kit.",
            schema,
            metadata);
    }

    public override JsonElement Apply(PendingInput input, JsonElement answer)
    {
        var model = store.GetEquipment(input.Question.EquipmentId)?.Model ?? string.Empty;
        return Result(new FanMotorReading(
            input.Question.EquipmentId,
            answer.GetProperty("amps").GetDecimal(),
            RatedAmps.TryGetValue(model, out var rated) ? rated : null,
            answer.GetProperty("noise").GetString()!,
            answer.TryGetProperty("shaftPlay", out var play) && play.GetBoolean()));
    }
}

/// <summary>What the technician measured, with the motor's rating for comparison.</summary>
public sealed record FanMotorReading(string EquipmentId, decimal Amps, decimal? RatedAmps, string Noise, bool ShaftPlay);
