using Beacon.Domain;

namespace Ch12.Beacon.Agent.Sheet;

/// <summary>
/// Beacon's shared state: the work-order sheet the dispatcher and the agent edit together. It is the
/// whole of <c>RunAgentInput.state</c> and of every <c>STATE_SNAPSHOT</c>, so everything in it is
/// visible to the client and comes back on every run. It holds what the sheet shows and nothing else:
/// no phone numbers, addresses, serial numbers, prices, spending limits or flags the server trusts.
/// </summary>
public sealed record SheetState
{
    /// <summary>The shape this server writes. Version 2 added <see cref="WorkOrderSheet.Notes"/>.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The document's shape. Beacon Field's first build didn't write it, so a missing value means 1.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The work order on the sheet, or <see langword="null"/> when none is open.</summary>
    public WorkOrderSheet? WorkOrder { get; init; }
}

/// <summary>One work order as the sheet shows it.</summary>
public sealed record WorkOrderSheet
{
    // Owned by the server: projected from the work-order system on every run, whatever the client sent.
    public required string Id { get; init; }
    public required string Customer { get; init; }
    public required string Equipment { get; init; }
    public required string Summary { get; init; }
    public required WorkOrderPriority Priority { get; init; }
    public required string Technician { get; init; }

    // Shared: the dispatcher and the agent both edit these.
    public required WorkOrderStatus Status { get; init; }
    public required IReadOnlyList<ChecklistItem> Checklist { get; init; }

    /// <summary>Added in schema version 2; a version 1 sheet has none.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}
