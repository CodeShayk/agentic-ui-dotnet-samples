using System.ComponentModel;
using Beacon.AI;
using Beacon.Domain;
using Ch15.Beacon.Agent.Input;
using Microsoft.Extensions.AI;

namespace Ch15.Beacon.Agent;

/// <summary>
/// Beacon's read-only tools for this chapter. They are the shared ones, except that the warranty check
/// works the way Contoso Air's registry does: by serial number. Equipment without one on record can't be
/// checked, and the tool says so in words that point the model at the question to ask.
/// </summary>
public sealed class BackendTools(IFieldServiceStore store)
{
    public IList<AITool> Create() =>
    [
        .. new BeaconTools(store).CreateReadOnlyTools().Where(t => t.Name != BeaconTools.CheckWarrantyName),
        AIFunctionFactory.Create(CheckWarranty, BeaconTools.CheckWarrantyName,
            "Gets the warranty status of a piece of equipment. The manufacturer looks warranty up by serial number.",
            Ch15Json.Options),
    ];

    public object CheckWarranty([Description("The equipment ID, for example EQ-501.")] string equipmentId) =>
        store.GetEquipment(equipmentId) switch
        {
            null => new ToolError($"Equipment {equipmentId} was not found."),
            { SerialNumber: null } equipment => new ToolError(
                $"{equipment.Id} has no serial number on record, and {equipment.Manufacturer} looks warranty up by serial " +
                $"number. Ask the technician for it with {SerialNumberForm.Name}."),
            var equipment => store.GetWarranty(equipment.Id) is { } warranty
                ? new SerialWarranty(equipment.Id, equipment.SerialNumber, warranty.Covered, warranty.ExpiresOn, warranty.Coverage)
                : new ToolError($"No warranty record for {equipment.Id}."),
        };
}

/// <summary>A warranty, with the serial number it was looked up by.</summary>
public sealed record SerialWarranty(string EquipmentId, string SerialNumber, bool Covered, DateOnly? ExpiresOn, string Coverage);
