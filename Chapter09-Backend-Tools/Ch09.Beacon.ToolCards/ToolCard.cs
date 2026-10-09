namespace Ch09.Beacon.ToolCards;

/// <summary>Where a tool call is, in the words a status chip uses.</summary>
public enum ToolCardState
{
    /// <summary>The model is still writing the call's arguments.</summary>
    Preparing,

    /// <summary>The arguments are complete and the tool is running.</summary>
    Running,

    /// <summary>A result arrived.</summary>
    Succeeded,

    /// <summary>A result arrived, and it says the tool failed.</summary>
    Failed,

    /// <summary>The run ended before the tool answered (Stop, a lost connection, a failed run).</summary>
    Stopped,

    /// <summary>The call waits for someone: an approval (Chapter 14) or the client itself (Chapter 10).</summary>
    Waiting,
}

/// <summary>
/// One tool call as a person sees it: a headline for the status chip and, once the result is in, a
/// typed body for a card. Built from <c>Beacon.Client.Core</c>'s <c>ToolCallView</c> by
/// <see cref="ToolCards.From"/>. Every string in it came from the model or a tool, so render it as text.
/// </summary>
/// <param name="CallId">The tool call's ID, a stable render key.</param>
/// <param name="ToolName">The tool's name, such as <c>get_work_order</c>.</param>
/// <param name="State">Where the call is.</param>
/// <param name="Headline">What the chip says, such as "Looking up WO-1001".</param>
public sealed record ToolCard(string CallId, string ToolName, ToolCardState State, string Headline)
{
    /// <summary>Why the tool failed, in words a dispatcher can act on, when <see cref="State"/> is <see cref="ToolCardState.Failed"/>.</summary>
    public string? Error { get; init; }

    /// <summary>The result, when it's one this client has a card for; otherwise <see langword="null"/>.</summary>
    public ToolResultBody? Body { get; init; }

    /// <summary>True while the call can still change on its own.</summary>
    public bool IsBusy => State is ToolCardState.Preparing or ToolCardState.Running;
}

/// <summary>A tool result this client knows how to show.</summary>
public abstract record ToolResultBody;

/// <summary><c>get_work_order</c>: the job, the customer and the unit.</summary>
public sealed record WorkOrderBody(
    string Id,
    string Status,
    string Priority,
    string Summary,
    string Customer,
    string EquipmentId,
    string Equipment,
    DateTimeOffset? ScheduledFor,
    string? TechnicianId,
    int ChecklistDone,
    int ChecklistTotal) : ToolResultBody;

/// <summary>One row of the parts table.</summary>
public sealed record PartRow(string Sku, string Name, decimal UnitPrice, int InStock, string Warehouse);

/// <summary><c>search_parts</c>: the parts that matched, at most <see cref="ToolCards.MaxPartRows"/> of them.</summary>
public sealed record PartsBody(IReadOnlyList<PartRow> Parts, int TotalCount) : ToolResultBody;

/// <summary><c>check_warranty</c>: whether the unit is covered, and how.</summary>
public sealed record WarrantyBody(string EquipmentId, bool Covered, string Coverage, DateOnly? ExpiresOn) : ToolResultBody;

/// <summary><c>set_checklist_item</c>: the item as it now stands.</summary>
public sealed record ChecklistBody(string WorkOrderId, string ItemId, string Text, bool Done, bool Changed, int DoneCount, int ItemCount) : ToolResultBody;

/// <summary>A result that isn't JSON at all, shown as text. A sign that the tool didn't follow Beacon's result contract.</summary>
public sealed record TextBody(string Text) : ToolResultBody;
