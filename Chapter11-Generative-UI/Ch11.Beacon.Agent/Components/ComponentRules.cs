using System.Globalization;
using System.Text.Json;
using Beacon.Domain;
using Ch11.Beacon.Components;

namespace Ch11.Beacon.Agent.Components;

/// <summary>
/// The server's checks on component props that already match their schema. A schema can say a SKU looks
/// like a SKU; only the server can say the part exists, costs what the card claims and fits the unit.
/// </summary>
/// <remarks>
/// The rule for every component is the same: props may only restate data Beacon owns. Facts the
/// server can look up (a name, a price, a description) are corrected and reported; references it
/// can't resolve (a SKU, a technician, a slot or a fault that isn't on record) reject the component.
/// The props in an accepted result are the server's version, and they are what clients render.
/// </remarks>
public sealed class ComponentRules(IFieldServiceStore store)
{
    public ComponentResult PartCard(JsonElement json)
    {
        var props = (PartCardProps)json.Deserialize(BeaconComponents.PartCard.PropsType)!;
        if (store.GetPart(props.Sku!) is not { } part)
        {
            return Reject(BeaconComponents.PartCard, $"$.sku: {props.Sku} isn't a stocked part. Use a SKU from search_parts.");
        }

        WorkOrder? workOrder = null;
        if (props.WorkOrderId is { } workOrderId)
        {
            workOrder = store.GetWorkOrder(workOrderId);
            if (workOrder is null)
            {
                return Reject(BeaconComponents.PartCard, $"$.workOrderId: {workOrderId} doesn't exist.");
            }

            var model = store.GetEquipment(workOrder.EquipmentId)?.Model;
            if (model is null || !part.CompatibleModels.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                return Reject(BeaconComponents.PartCard, $"$.sku: {part.Sku} doesn't fit the {model} on {workOrder.Id}.");
            }
        }

        List<string> corrections = [];
        Correct(corrections, "$.name", props.Name, part.Name);
        Correct(corrections, "$.unitPrice", props.UnitPrice, part.UnitPrice);
        Correct(corrections, "$.inStock", props.InStock, part.InStock);
        Correct(corrections, "$.warehouse", props.Warehouse, part.Warehouse);

        var canonical = props with { Sku = part.Sku, Name = part.Name, UnitPrice = part.UnitPrice, InStock = part.InStock, Warehouse = part.Warehouse };
        return ComponentResult.Accepted(
            BeaconComponents.PartCard.ComponentName,
            Serialize(canonical, BeaconComponents.PartCard),
            workOrder is null ? null : [OrderAction(part)],
            corrections);
    }

    public ComponentResult ScheduleTimeline(JsonElement json)
    {
        var props = (ScheduleTimelineProps)json.Deserialize(BeaconComponents.ScheduleTimeline.PropsType)!;
        var date = DateOnly.ParseExact(props.Date!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        List<string> errors = [];
        List<string> corrections = [];
        List<TimelineLane> lanes = [];

        foreach (var (lane, i) in props.Lanes!.Select((lane, i) => (lane, i)))
        {
            var technician = store.ListTechnicians().FirstOrDefault(t => string.Equals(t.Id, lane.TechnicianId, StringComparison.OrdinalIgnoreCase));
            if (technician is null)
            {
                errors.Add($"$.lanes[{i}].technicianId: {lane.TechnicianId} isn't a Contoso Climate technician.");
                continue;
            }

            var recorded = store.GetSchedule(date, technician.Id);
            foreach (var (slot, j) in lane.Slots!.Select((slot, j) => (slot, j)))
            {
                var match = recorded.Any(r => Clock(r.Start) == slot.Start && Clock(r.End) == slot.End
                                              && string.Equals(r.WorkOrderId, slot.WorkOrderId, StringComparison.OrdinalIgnoreCase));
                if (!match)
                {
                    errors.Add($"$.lanes[{i}].slots[{j}]: {technician.Id} has no {(slot.WorkOrderId is null ? "free slot" : slot.WorkOrderId)} from {slot.Start} to {slot.End} on {props.Date}.");
                }
            }

            if (lane.TechnicianName is not null)
            {
                Correct(corrections, $"$.lanes[{i}].technicianName", lane.TechnicianName, technician.Name);
            }

            lanes.Add(lane with { TechnicianId = technician.Id, TechnicianName = technician.Name, Slots = [.. lane.Slots!.OrderBy(s => s.Start, StringComparer.Ordinal)] });
        }

        return errors.Count > 0
            ? ComponentResult.Rejected(BeaconComponents.ScheduleTimeline.ComponentName, errors)
            : ComponentResult.Accepted(
                BeaconComponents.ScheduleTimeline.ComponentName,
                Serialize(props with { Lanes = lanes }, BeaconComponents.ScheduleTimeline),
                corrections: corrections);
    }

    public ComponentResult FaultHistoryChart(JsonElement json)
    {
        var props = (FaultHistoryChartProps)json.Deserialize(BeaconComponents.FaultHistoryChart.PropsType)!;
        if (store.GetEquipment(props.EquipmentId!) is not { } equipment)
        {
            return Reject(BeaconComponents.FaultHistoryChart, $"$.equipmentId: {props.EquipmentId} doesn't exist.");
        }

        var recorded = store.GetFaultHistory(equipment.Id);
        List<string> errors = [];
        List<string> corrections = [];
        List<FaultPoint> faults = [];

        foreach (var (fault, i) in props.Faults!.Select((fault, i) => (fault, i)))
        {
            var record = recorded.FirstOrDefault(r => r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == fault.Date && r.Code == fault.Code);
            if (record is null)
            {
                errors.Add($"$.faults[{i}]: {equipment.Id} has no {fault.Code} fault on record for {fault.Date}.");
                continue;
            }

            Correct(corrections, $"$.faults[{i}].description", fault.Description, record.Description);
            faults.Add(fault with { Description = record.Description });
        }

        return errors.Count > 0
            ? ComponentResult.Rejected(BeaconComponents.FaultHistoryChart.ComponentName, errors)
            : ComponentResult.Accepted(
                BeaconComponents.FaultHistoryChart.ComponentName,
                Serialize(props with { EquipmentId = equipment.Id, Faults = [.. faults.OrderBy(f => f.Date, StringComparer.Ordinal)] }, BeaconComponents.FaultHistoryChart),
                corrections: corrections);
    }

    // Which buttons the card may show is the server's call too: the client renders the actions it is
    // given, and the order itself still goes through place_part_order and its own checks.
    private static ComponentAction OrderAction(Part part) =>
        part.InStock == 0 ? new("order", "Order this part", Enabled: false, Reason: "Out of stock")
        : SpendingPolicy.RequiresApproval(part.UnitPrice) ? new("order", "Order this part", Enabled: false, Reason: "Over $500: needs approval")
        : new("order", "Order this part", Enabled: true);

    private static ComponentResult Reject(ComponentContract contract, string error) =>
        ComponentResult.Rejected(contract.ComponentName, [error]);

    // A value the model left out isn't a correction; one it got wrong is, and the model hears about it.
    private static void Correct(List<string> corrections, string path, string? claimed, string actual)
    {
        if (claimed is not null && !string.Equals(claimed, actual, StringComparison.Ordinal))
        {
            corrections.Add($"{path}: corrected to {actual} from Beacon's records.");
        }
    }

    private static void Correct<T>(List<string> corrections, string path, T? claimed, T actual)
        where T : struct, IFormattable
    {
        if (claimed is { } value && !value.Equals(actual))
        {
            corrections.Add($"{path}: corrected to {actual.ToString(null, CultureInfo.InvariantCulture)} from Beacon's records.");
        }
    }

    private static string Clock(DateTimeOffset time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static JsonElement Serialize(object props, ComponentContract contract) =>
        JsonSerializer.SerializeToElement(props, contract.PropsType);
}
