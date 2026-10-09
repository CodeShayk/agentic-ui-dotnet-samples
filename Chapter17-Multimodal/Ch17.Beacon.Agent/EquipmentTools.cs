using System.ComponentModel;
using System.Text.RegularExpressions;
using Beacon.AI;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch17.Beacon.Agent;

/// <summary>The serial number is now on the equipment record.</summary>
public sealed record SerialNumberRecorded(string EquipmentId, string SerialNumber, bool Recorded);

/// <summary>
/// Records a serial number Beacon read from a nameplate photo. What a model reads from an image is a
/// claim, like anything else it produces. The instructions and this tool's description ask the model to
/// confirm the reading with the user first, but that is guidance, not control (Chapter 14): the hard limits
/// are here, in the shape Contoso Air prints and the refusal to overwrite a different number.
/// </summary>
public sealed class EquipmentTools(IFieldServiceStore store)
{
    public const string RecordSerialNumberName = "record_serial_number";

    public AIFunction Create() => AIFunctionFactory.Create(
        RecordSerialNumber,
        RecordSerialNumberName,
        "Records a serial number on an equipment record. Call it only after the user has confirmed the number, " +
        "for example one you read from a nameplate photo.",
        Ch17Json.Options);

    public object RecordSerialNumber(
        [Description("The equipment ID, for example EQ-601.")] string equipmentId,
        [Description("The serial number exactly as printed on the nameplate.")] string serialNumber)
    {
        if (store.GetEquipment(equipmentId) is not { } equipment)
        {
            return new ToolError($"Equipment {equipmentId} was not found.");
        }

        // Contoso Air prints the model without its dash, then two groups of four digits: HP24S-2207-1184.
        // \z, not $: in .NET, $ also matches before a final newline, so "HP24S-2207-1184\n" would pass (Chapter 15).
        var prefix = equipment.Model.Replace("-", string.Empty, StringComparison.Ordinal);
        if (equipment.Manufacturer == "Contoso Air" && !Regex.IsMatch(serialNumber, $@"^{Regex.Escape(prefix)}-[0-9]{{4}}-[0-9]{{4}}\z"))
        {
            return new ToolError($"{serialNumber} isn't a {equipment.Manufacturer} serial number for a {equipment.Model}; they look like {prefix}-####-####.");
        }

        if (equipment.SerialNumber is { } existing && existing != serialNumber)
        {
            return new ToolError($"{equipment.Id} already has serial number {existing} on record; it was not changed.");
        }

        store.UpdateEquipment(equipment with { SerialNumber = serialNumber });
        return new SerialNumberRecorded(equipment.Id, serialNumber, Recorded: true);
    }
}
