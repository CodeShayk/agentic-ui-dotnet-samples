using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ch10.Beacon.Agent.FrontendTools;

/// <summary>The tools Beacon's clients run themselves: three on the Desk, three in the Field.</summary>
public static class FrontendToolNames
{
    public const string OpenWorkOrder = "open_work_order";
    public const string HighlightScheduleSlot = "highlight_schedule_slot";
    public const string PrefillPartOrder = "prefill_part_order";
    public const string GetLocation = "get_location";
    public const string TakePhoto = "take_photo";
    public const string ScanPartLabel = "scan_part_label";
}

// The agent's own picture of each result. It shares no types with the clients: it writes down what it
// will accept, and a client that sends anything else gets a rejection, not a seat in the prompt.
// Results carry statuses, identifiers and numbers, never prose, because prose is where instructions hide.
public sealed record OpenWorkOrderResult(string Status, string? WorkOrderId = null);

public sealed record HighlightSlotResult(string Status, string? TechnicianId = null, DateTimeOffset? Start = null);

public sealed record PrefillPartOrderResult(string Status, string? WorkOrderId = null, string? Sku = null, int? Quantity = null);

public sealed record LocationResult(
    string Status, double? Latitude = null, double? Longitude = null, double? AccuracyMeters = null, DateTimeOffset? CapturedAt = null);

public sealed record PhotoResult(string Status, string? PhotoId = null, string? WorkOrderId = null, long? SizeBytes = null);

public sealed record PartLabelResult(string Status, string? Sku = null, string? Method = null);

/// <summary>
/// Strict, source-generated parsing for frontend tool results: a missing <c>status</c> or a <c>null</c>
/// where the record doesn't allow one fails to parse, and unknown properties are dropped on the way back out.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(OpenWorkOrderResult))]
[JsonSerializable(typeof(HighlightSlotResult))]
[JsonSerializable(typeof(PrefillPartOrderResult))]
[JsonSerializable(typeof(LocationResult))]
[JsonSerializable(typeof(PhotoResult))]
[JsonSerializable(typeof(PartLabelResult))]
public sealed partial class FrontendResultJsonContext : JsonSerializerContext;

/// <summary>Beacon's frontend tool allow-list: a tool without a contract is never offered to the model, and its results are never accepted.</summary>
public sealed partial class FrontendToolContracts
{
    private const string UnknownStatus = "the status isn't one this tool reports";

    private readonly Dictionary<string, FrontendToolContract> _contracts;

    public FrontendToolContracts(IEnumerable<FrontendToolContract> contracts) =>
        _contracts = contracts.ToDictionary(c => c.Name, StringComparer.Ordinal);

    /// <summary>The contracts for Beacon Desk's and Beacon Field's tools.</summary>
    public static FrontendToolContracts Beacon { get; } = new(
    [
        FrontendToolContract.For(FrontendToolNames.OpenWorkOrder, FrontendResultJsonContext.Default.OpenWorkOrderResult, r => r.Status switch
        {
            "opened" => WorkOrderId().IsMatch(r.WorkOrderId ?? "") ? null : "workOrderId must look like WO-1001",
            "not-found" => null,
            _ => UnknownStatus,
        }),

        FrontendToolContract.For(FrontendToolNames.HighlightScheduleSlot, FrontendResultJsonContext.Default.HighlightSlotResult, r => r.Status switch
        {
            "highlighted" when !TechnicianId().IsMatch(r.TechnicianId ?? "") => "technicianId must look like TECH-1",
            "highlighted" => r.Start is null ? "a highlighted slot needs its start time" : null,
            "not-found" => null,
            _ => UnknownStatus,
        }),

        FrontendToolContract.For(FrontendToolNames.PrefillPartOrder, FrontendResultJsonContext.Default.PrefillPartOrderResult, r => r.Status switch
        {
            "prefilled" when !WorkOrderId().IsMatch(r.WorkOrderId ?? "") => "workOrderId must look like WO-1001",
            "prefilled" when !Sku().IsMatch(r.Sku ?? "") => "sku must look like CAP-45-5",
            "prefilled" => r.Quantity is >= 1 and <= 10 ? null : "quantity must be from 1 to 10",
            "declined" or "no-answer" or "not-found" => null,
            _ => UnknownStatus,
        }),

        FrontendToolContract.For(FrontendToolNames.GetLocation, FrontendResultJsonContext.Default.LocationResult, r => r.Status switch
        {
            "located" when r.Latitude is not (>= -90 and <= 90) => "latitude must be between -90 and 90",
            "located" when r.Longitude is not (>= -180 and <= 180) => "longitude must be between -180 and 180",
            "located" => r.AccuracyMeters is null or (>= 0 and <= 100_000) ? null : "accuracyMeters must be between 0 and 100000",
            "declined" or "permission-denied" or "unavailable" => null,
            _ => UnknownStatus,
        }),

        FrontendToolContract.For(FrontendToolNames.TakePhoto, FrontendResultJsonContext.Default.PhotoResult, r => r.Status switch
        {
            "captured" when !PhotoId().IsMatch(r.PhotoId ?? "") => "photoId must look like photo-0123456789ab",
            "captured" when r.WorkOrderId is not null && !WorkOrderId().IsMatch(r.WorkOrderId) => "workOrderId must look like WO-1001",
            "captured" => r.SizeBytes is > 0 and <= 25_000_000 ? null : "sizeBytes must be between 1 and 25000000",
            "declined" or "permission-denied" or "unavailable" or "cancelled" or "invalid" => null,
            _ => UnknownStatus,
        }),

        FrontendToolContract.For(FrontendToolNames.ScanPartLabel, FrontendResultJsonContext.Default.PartLabelResult, r => r.Status switch
        {
            "read" when !Sku().IsMatch(r.Sku ?? "") => "sku must look like CAP-45-5",
            "read" => r.Method is "manual" or "scanner" ? null : "method must be manual or scanner",
            "declined" or "cancelled" or "unreadable" => null,
            _ => UnknownStatus,
        }),
    ]);

    /// <summary>The names of the tools with a contract.</summary>
    public IReadOnlyCollection<string> Names => _contracts.Keys;

    /// <summary>Whether Beacon has a contract for <paramref name="toolName"/>.</summary>
    public bool Contains(string toolName) => _contracts.ContainsKey(toolName);

    /// <summary>Finds the contract for <paramref name="toolName"/>.</summary>
    public bool TryGet(string toolName, [NotNullWhen(true)] out FrontendToolContract? contract) =>
        _contracts.TryGetValue(toolName, out contract);

    [GeneratedRegex(@"^WO-\d{4}$")]
    private static partial Regex WorkOrderId();

    [GeneratedRegex(@"^TECH-\d{1,3}$")]
    private static partial Regex TechnicianId();

    [GeneratedRegex(@"^[A-Z]{3}-[A-Z0-9]{1,8}(?:-[A-Z0-9]{1,8})?$")]
    private static partial Regex Sku();

    [GeneratedRegex(@"^photo-[0-9a-f]{12}$")]
    private static partial Regex PhotoId();
}
