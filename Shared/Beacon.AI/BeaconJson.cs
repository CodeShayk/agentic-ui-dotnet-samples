using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Beacon.AI;

/// <summary>Source-generated serialization for Beacon's domain types (AOT- and trim-safe).</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkOrder))]
[JsonSerializable(typeof(IReadOnlyList<WorkOrder>))]
[JsonSerializable(typeof(WorkOrderDetails))]
[JsonSerializable(typeof(Customer))]
[JsonSerializable(typeof(Equipment))]
[JsonSerializable(typeof(Part))]
[JsonSerializable(typeof(IReadOnlyList<Part>))]
[JsonSerializable(typeof(Technician))]
[JsonSerializable(typeof(IReadOnlyList<Technician>))]
[JsonSerializable(typeof(ScheduleSlot))]
[JsonSerializable(typeof(IReadOnlyList<ScheduleSlot>))]
[JsonSerializable(typeof(WarrantyStatus))]
[JsonSerializable(typeof(FaultRecord))]
[JsonSerializable(typeof(IReadOnlyList<FaultRecord>))]
[JsonSerializable(typeof(PartOrder))]
[JsonSerializable(typeof(CustomerCharge))]
[JsonSerializable(typeof(ChecklistItem))]
[JsonSerializable(typeof(ToolError))]
[JsonSerializable(typeof(WorkOrderStatus?))]
public sealed partial class BeaconJsonContext : JsonSerializerContext;

/// <summary>Serializer options for Beacon's tools: Beacon's types first, then Microsoft.Extensions.AI's defaults.</summary>
public static class BeaconJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.TypeInfoResolverChain.Insert(0, BeaconJsonContext.Default);
        options.MakeReadOnly();
        return options;
    }
}
