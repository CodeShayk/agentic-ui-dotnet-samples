using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Beacon.AI;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch15.Beacon.Agent.Input;

/// <summary>
/// Asks the technician for a serial number Beacon doesn't have. The schema is built for the unit in
/// question: Contoso Air prints its serials as the model without the dash and two groups of four digits
/// (CA36X-2219-0441), so for EQ-601, an HP-24S, nothing but HP24S-####-#### is accepted.
/// </summary>
public sealed class SerialNumberForm(IFieldServiceStore store) : InputForm
{
    public const string FormId = "serial-number";
    public const string Name = "ask_for_serial_number";

    public override string Id => FormId;

    public override string ToolName => Name;

    public override AIFunction CreateTool() => AIFunctionFactory.Create(
        OnRecord,
        Name,
        "Asks the technician on site to read the serial number from the equipment's nameplate. The run pauses until " +
        "they answer. Use it when a tool reports that the equipment has no serial number on record; never guess one.",
        Ch15Json.Options);

    // Runs only when nothing needed asking: the number is on record already, or there is no such unit.
    private object OnRecord([Description("The equipment ID, for example EQ-601.")] string equipmentId) =>
        store.GetEquipment(equipmentId) switch
        {
            null => new ToolError($"Equipment {equipmentId} was not found."),
            { SerialNumber: { } serial } equipment => new SerialNumberOnRecord(equipment.Id, serial),
            _ => new ToolError("Only the technician can supply the serial number; ask again."),
        };

    public override InputQuestion? Ask(IDictionary<string, object?>? arguments)
    {
        // Ask only about equipment that exists and really has no serial number.
        if (store.GetEquipment(Argument(arguments, "equipmentId")) is not { SerialNumber: null } equipment)
        {
            return null;
        }

        var (pattern, length, shape) = SerialFormat(equipment);
        var site = store.GetCustomer(equipment.CustomerId)?.Name ?? "the customer's site";

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["title"] = $"Serial number for {equipment.Id}",
            ["properties"] = new JsonObject
            {
                ["serialNumber"] = new JsonObject
                {
                    ["type"] = "string",
                    ["title"] = "Serial number",
                    ["description"] = $"As printed on the nameplate: {shape}.",
                    ["pattern"] = pattern,
                    ["minLength"] = length.Min,
                    ["maxLength"] = length.Max,
                },
                ["readFrom"] = new JsonObject
                {
                    ["type"] = "string",
                    ["title"] = "Where did you read it?",
                    ["enum"] = new JsonArray("nameplate", "paperwork", "customer"),
                },
                ["nameplatePhotoTaken"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["title"] = "I photographed the nameplate",
                    ["default"] = false,
                },
            },
            ["required"] = new JsonArray("serialNumber", "readFrom"),
            ["additionalProperties"] = false,
        };

        return Question(
            equipment.Id,
            $"{equipment.Id}, the {equipment.Manufacturer} {equipment.Model} {Describe(equipment.Kind)} at {site}, has no serial number " +
            "on record. Read it from the nameplate so Beacon can check the warranty.",
            schema,
            new JsonObject { ["form"] = FormId, ["equipmentId"] = equipment.Id, ["model"] = equipment.Model });
    }

    public override JsonElement Apply(PendingInput input, JsonElement answer)
    {
        var serial = answer.GetProperty("serialNumber").GetString()!;
        var readFrom = answer.GetProperty("readFrom").GetString()!;
        var photo = answer.TryGetProperty("nameplatePhotoTaken", out var taken) && taken.GetBoolean();

        if (store.GetEquipment(input.Question.EquipmentId) is not { } equipment)
        {
            return Result(new ToolError($"Equipment {input.Question.EquipmentId} no longer exists."));
        }

        // Someone may have recorded it since the question was asked. Never overwrite a different number.
        if (equipment.SerialNumber is { } existing && existing != serial)
        {
            return Result(new ToolError($"{equipment.Id} already has serial number {existing} on record; it was not changed."));
        }

        store.UpdateEquipment(equipment with { SerialNumber = serial });
        return Result(new SerialNumberRecorded(equipment.Id, serial, readFrom, photo, Recorded: true));
    }

    // The pattern uses [0-9], not \d: it must mean the same in every validator that reads it.
    private static (string Pattern, (int Min, int Max) Length, string Shape) SerialFormat(Equipment equipment)
    {
        if (equipment.Manufacturer == "Contoso Air")
        {
            var prefix = equipment.Model.Replace("-", string.Empty, StringComparison.Ordinal);
            var length = prefix.Length + 10;
            return ($"^{Regex.Escape(prefix)}-[0-9]{{4}}-[0-9]{{4}}$", (length, length), $"{prefix}-####-####");
        }

        return ("^[A-Z0-9][A-Z0-9-]{5,29}$", (6, 30), "capital letters, digits and dashes");
    }

    private static string Describe(EquipmentKind kind) => kind switch
    {
        EquipmentKind.AirConditioner => "air conditioner",
        EquipmentKind.HeatPump => "heat pump",
        EquipmentKind.RooftopUnit => "rooftop unit",
        _ => kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>The serial number was already on record; nothing needed asking.</summary>
public sealed record SerialNumberOnRecord(string EquipmentId, string SerialNumber, string Source = "equipment record");

/// <summary>The technician's answer, recorded on the equipment.</summary>
public sealed record SerialNumberRecorded(string EquipmentId, string SerialNumber, string ReadFrom, bool NameplatePhotoTaken, bool Recorded);
