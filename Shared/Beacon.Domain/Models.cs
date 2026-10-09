namespace Beacon.Domain;

/// <summary>A Contoso Climate customer and the site where its equipment is installed.</summary>
public sealed record Customer(
    string Id,
    string Name,
    string Address,
    string Phone,
    ServiceTier Tier,
    double Latitude,
    double Longitude);

public enum ServiceTier { Standard, Priority, Premium }

/// <summary>A heating or cooling unit installed at a customer site.</summary>
public sealed record Equipment(
    string Id,
    string CustomerId,
    EquipmentKind Kind,
    string Manufacturer,
    string Model,
    string? SerialNumber,
    DateOnly InstalledOn);

public enum EquipmentKind { AirConditioner, HeatPump, Furnace, Boiler, RooftopUnit }

/// <summary>A job to inspect, diagnose or repair one piece of equipment.</summary>
public sealed record WorkOrder(
    string Id,
    string CustomerId,
    string EquipmentId,
    string Summary,
    WorkOrderStatus Status,
    WorkOrderPriority Priority,
    string? AssignedTechnicianId,
    DateTimeOffset? ScheduledFor,
    IReadOnlyList<ChecklistItem> Checklist,
    IReadOnlyList<string> Notes);

public enum WorkOrderStatus { Open, Scheduled, InProgress, AwaitingParts, Completed, Cancelled }

public enum WorkOrderPriority { Low, Normal, High, Emergency }

public sealed record ChecklistItem(string Id, string Text, bool Done);

/// <summary>A stocked part, with the equipment models it fits.</summary>
public sealed record Part(
    string Sku,
    string Name,
    decimal UnitPrice,
    int InStock,
    string Warehouse,
    IReadOnlyList<string> CompatibleModels);

public sealed record Technician(
    string Id,
    string Name,
    string Region,
    IReadOnlyList<string> Skills);

/// <summary>A block of a technician's day, either free or assigned to a work order.</summary>
public sealed record ScheduleSlot(
    string TechnicianId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string? WorkOrderId);

public sealed record WarrantyStatus(
    string EquipmentId,
    bool Covered,
    DateOnly? ExpiresOn,
    string Coverage);

public sealed record FaultRecord(
    string EquipmentId,
    DateOnly Date,
    string Code,
    string Description);

/// <summary>An order for replacement parts against a work order.</summary>
public sealed record PartOrder(
    string Id,
    string WorkOrderId,
    string Sku,
    int Quantity,
    decimal Total,
    PartOrderStatus Status,
    DateTimeOffset PlacedAt);

public enum PartOrderStatus { Placed, Shipped, Delivered, Cancelled }

/// <summary>A charge to a customer's account for completed work.</summary>
public sealed record CustomerCharge(
    string Id,
    string CustomerId,
    string WorkOrderId,
    decimal Amount,
    string Description,
    DateTimeOffset ChargedAt);
