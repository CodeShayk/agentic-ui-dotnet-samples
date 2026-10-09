namespace Ch11.Beacon.Components;

// The props of Beacon's components, as typed records. Every member is nullable on purpose: a client
// deserializes the same records from a half-streamed argument text (section 11.5), where any field may
// still be missing. The prop schemas, not these types, decide what a complete component must have.

/// <summary>Props of the part card: one stocked part, optionally for a work order.</summary>
public sealed record PartCardProps
{
    public string? Sku { get; init; }

    public string? Name { get; init; }

    public decimal? UnitPrice { get; init; }

    public int? InStock { get; init; }

    public string? Warehouse { get; init; }

    public string? WorkOrderId { get; init; }

    public string? Note { get; init; }
}

/// <summary>Props of the schedule timeline: one day, one lane per technician.</summary>
public sealed record ScheduleTimelineProps
{
    public string? Date { get; init; }

    public string? Title { get; init; }

    public IReadOnlyList<TimelineLane>? Lanes { get; init; }
}

/// <summary>One technician's lane on the timeline.</summary>
public sealed record TimelineLane
{
    public string? TechnicianId { get; init; }

    public string? TechnicianName { get; init; }

    public IReadOnlyList<TimelineSlot>? Slots { get; init; }
}

/// <summary>A block of a technician's day: a job when it names a work order, free time otherwise.</summary>
public sealed record TimelineSlot
{
    public string? Start { get; init; }

    public string? End { get; init; }

    public string? WorkOrderId { get; init; }

    public string? Label { get; init; }
}

/// <summary>Props of the fault history chart: one piece of equipment and its recorded faults.</summary>
public sealed record FaultHistoryChartProps
{
    public string? EquipmentId { get; init; }

    public string? Title { get; init; }

    public IReadOnlyList<FaultPoint>? Faults { get; init; }
}

/// <summary>One recorded fault.</summary>
public sealed record FaultPoint
{
    public string? Date { get; init; }

    public string? Code { get; init; }

    public string? Description { get; init; }
}
