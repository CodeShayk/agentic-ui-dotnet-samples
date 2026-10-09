using System.Collections.Concurrent;

namespace Beacon.Domain;

/// <summary>
/// The operations Beacon's tools perform. Chapters expose these as AI tools; they never
/// trust the model to enforce business rules, so every rule lives here or in the tool.
/// </summary>
public interface IFieldServiceStore
{
    WorkOrder? GetWorkOrder(string workOrderId);
    IReadOnlyList<WorkOrder> ListWorkOrders(WorkOrderStatus? status = null, string? technicianId = null);
    WorkOrder UpdateWorkOrder(WorkOrder workOrder);
    Customer? GetCustomer(string customerId);
    Equipment? GetEquipment(string equipmentId);
    Equipment UpdateEquipment(Equipment equipment);
    IReadOnlyList<Part> SearchParts(string query, string? model = null);
    Part? GetPart(string sku);
    WarrantyStatus? GetWarranty(string equipmentId);
    IReadOnlyList<FaultRecord> GetFaultHistory(string equipmentId);
    IReadOnlyList<Technician> ListTechnicians();
    IReadOnlyList<ScheduleSlot> GetSchedule(DateOnly date, string? technicianId = null);
    PartOrder PlacePartOrder(string workOrderId, string sku, int quantity);
    IReadOnlyList<PartOrder> ListPartOrders(string? workOrderId = null);
    CustomerCharge ChargeCustomer(string workOrderId, decimal amount, string description);
}

/// <summary>Thrown when a request breaks a business rule; tools turn it into a message for the model.</summary>
public sealed class FieldServiceException(string message) : Exception(message);

/// <summary>A thread-safe, in-memory store seeded from <see cref="SeedData"/>.</summary>
public sealed class InMemoryFieldServiceStore : IFieldServiceStore
{
    private readonly ConcurrentDictionary<string, WorkOrder> _workOrders;
    private readonly ConcurrentDictionary<string, Equipment> _equipment;
    private readonly ConcurrentDictionary<string, Part> _parts;
    private readonly ConcurrentDictionary<string, PartOrder> _orders = new();
    private readonly ConcurrentDictionary<string, CustomerCharge> _charges = new();
    private int _nextOrder = 9000;
    private int _nextCharge = 7000;

    public InMemoryFieldServiceStore()
    {
        _workOrders = new(SeedData.WorkOrders.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        _equipment = new(SeedData.Equipment.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        _parts = new(SeedData.Parts.ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    public WorkOrder? GetWorkOrder(string workOrderId) =>
        _workOrders.TryGetValue(workOrderId, out var wo) ? wo : null;

    public IReadOnlyList<WorkOrder> ListWorkOrders(WorkOrderStatus? status = null, string? technicianId = null) =>
        [.. _workOrders.Values
            .Where(w => status is null || w.Status == status)
            .Where(w => technicianId is null || string.Equals(w.AssignedTechnicianId, technicianId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.Id)];

    public WorkOrder UpdateWorkOrder(WorkOrder workOrder)
    {
        if (!_workOrders.ContainsKey(workOrder.Id))
        {
            throw new FieldServiceException($"Work order {workOrder.Id} does not exist.");
        }

        _workOrders[workOrder.Id] = workOrder;
        return workOrder;
    }

    public Customer? GetCustomer(string customerId) =>
        SeedData.Customers.FirstOrDefault(c => string.Equals(c.Id, customerId, StringComparison.OrdinalIgnoreCase));

    public Equipment? GetEquipment(string equipmentId) =>
        _equipment.TryGetValue(equipmentId, out var eq) ? eq : null;

    public Equipment UpdateEquipment(Equipment equipment)
    {
        if (!_equipment.ContainsKey(equipment.Id))
        {
            throw new FieldServiceException($"Equipment {equipment.Id} does not exist.");
        }

        _equipment[equipment.Id] = equipment;
        return equipment;
    }

    public IReadOnlyList<Part> SearchParts(string query, string? model = null)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. _parts.Values
            .Where(p => model is null || p.CompatibleModels.Contains(model, StringComparer.OrdinalIgnoreCase))
            .Where(p => terms.Length == 0 || terms.Any(t =>
                p.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                p.Sku.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Sku)];
    }

    public Part? GetPart(string sku) => _parts.TryGetValue(sku, out var part) ? part : null;

    public WarrantyStatus? GetWarranty(string equipmentId) =>
        SeedData.Warranties.FirstOrDefault(w => string.Equals(w.EquipmentId, equipmentId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<FaultRecord> GetFaultHistory(string equipmentId) =>
        [.. SeedData.FaultHistory
            .Where(f => string.Equals(f.EquipmentId, equipmentId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Date)];

    public IReadOnlyList<Technician> ListTechnicians() => SeedData.Technicians;

    public IReadOnlyList<ScheduleSlot> GetSchedule(DateOnly date, string? technicianId = null) =>
        [.. SeedData.Schedule
            .Where(s => DateOnly.FromDateTime(s.Start.Date) == date)
            .Where(s => technicianId is null || string.Equals(s.TechnicianId, technicianId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.TechnicianId).ThenBy(s => s.Start)];

    public PartOrder PlacePartOrder(string workOrderId, string sku, int quantity)
    {
        if (quantity is < 1 or > 10)
        {
            throw new FieldServiceException("Quantity must be between 1 and 10.");
        }

        var workOrder = GetWorkOrder(workOrderId)
            ?? throw new FieldServiceException($"Work order {workOrderId} does not exist.");
        var part = GetPart(sku)
            ?? throw new FieldServiceException($"Part {sku} does not exist.");
        var equipment = GetEquipment(workOrder.EquipmentId)
            ?? throw new FieldServiceException($"Equipment {workOrder.EquipmentId} does not exist.");

        if (!part.CompatibleModels.Contains(equipment.Model, StringComparer.OrdinalIgnoreCase))
        {
            throw new FieldServiceException($"Part {sku} does not fit model {equipment.Model}.");
        }

        var order = new PartOrder(
            $"PO-{Interlocked.Increment(ref _nextOrder)}",
            workOrder.Id,
            part.Sku,
            quantity,
            part.UnitPrice * quantity,
            PartOrderStatus.Placed,
            DateTimeOffset.UtcNow);

        _orders[order.Id] = order;
        _workOrders[workOrder.Id] = workOrder with { Status = WorkOrderStatus.AwaitingParts };
        return order;
    }

    public IReadOnlyList<PartOrder> ListPartOrders(string? workOrderId = null) =>
        [.. _orders.Values
            .Where(o => workOrderId is null || string.Equals(o.WorkOrderId, workOrderId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Id)];

    public CustomerCharge ChargeCustomer(string workOrderId, decimal amount, string description)
    {
        if (amount is <= 0 or > 25_000m)
        {
            throw new FieldServiceException("Charge amount must be between 0 and 25,000.");
        }

        var workOrder = GetWorkOrder(workOrderId)
            ?? throw new FieldServiceException($"Work order {workOrderId} does not exist.");

        var charge = new CustomerCharge(
            $"CHG-{Interlocked.Increment(ref _nextCharge)}",
            workOrder.CustomerId,
            workOrder.Id,
            amount,
            description,
            DateTimeOffset.UtcNow);

        _charges[charge.Id] = charge;
        return charge;
    }
}
