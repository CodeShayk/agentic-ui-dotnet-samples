using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Ch10.Field.Tools;

/// <summary>What the technician is asked to photograph.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PhotoSubject>))]
public enum PhotoSubject
{
    [JsonStringEnumMemberName("nameplate")] Nameplate,
    [JsonStringEnumMemberName("part-label")] PartLabel,
    [JsonStringEnumMemberName("damage")] Damage,
    [JsonStringEnumMemberName("installation")] Installation,
}

/// <summary>What <c>get_location</c> reports: a rounded position, or why there isn't one.</summary>
public sealed record LocationResult(string Status, double? Latitude = null, double? Longitude = null, double? AccuracyMeters = null, DateTimeOffset? CapturedAt = null);

/// <summary>What <c>take_photo</c> reports: the photo's ID and size, never its pixels.</summary>
public sealed record PhotoResult(string Status, string? PhotoId = null, string? WorkOrderId = null, long? SizeBytes = null);

/// <summary>What <c>scan_part_label</c> reports: a well-formed part number, or why there isn't one.</summary>
public sealed record PartLabelResult(string Status, string? Sku = null, string? Method = null);

/// <summary>Source-generated serialization for the Field tools (Android and iOS trim).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LocationResult))]
[JsonSerializable(typeof(PhotoResult))]
[JsonSerializable(typeof(PartLabelResult))]
[JsonSerializable(typeof(PhotoSubject))]
public sealed partial class FieldToolsJsonContext : JsonSerializerContext;

/// <summary>
/// Beacon Field's frontend tools: things only the technician's device can do. Each tool follows the
/// same order: check the arguments, ask the technician, ask the operating system, then touch the
/// device, and report every outcome, refusals included, as a result the agent can explain.
/// </summary>
public sealed partial class FieldTools(
    ILocationSource location,
    ICameraCapture camera,
    IPartLabelReader labels,
    IDevicePermissions permissions,
    SessionConsent consent,
    JobAttachments attachments)
{
    public const string GetLocationName = "get_location";
    public const string TakePhotoName = "take_photo";
    public const string ScanPartLabelName = "scan_part_label";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>The tools, as <see cref="AIFunction"/>s for <c>ConversationOptions.Tools</c>.</summary>
    public IList<AITool> Create() =>
    [
        AIFunctionFactory.Create(GetLocationAsync, GetLocationName,
            "Reads the technician's current location from the device, after she agrees to share it.", JsonOptions),
        AIFunctionFactory.Create(TakePhotoAsync, TakePhotoName,
            "Asks the technician to take a photo with the device camera and attaches it to a work order.", JsonOptions),
        AIFunctionFactory.Create(ScanPartLabelAsync, ScanPartLabelName,
            "Asks the technician for the part number on a part's label and returns it.", JsonOptions),
    ];

    public async Task<LocationResult> GetLocationAsync(CancellationToken cancellationToken)
    {
        var request = new ConsentRequest(DeviceCapability.Location, "Share your location with Beacon?",
            "Beacon asked where you are, to check you're at the right site.", OfferSessionAllowance: true);
        if (!await consent.AllowsAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return new LocationResult("declined");
        }

        if (await permissions.EnsureAsync(DeviceCapability.Location, cancellationToken).ConfigureAwait(false) is not PermissionOutcome.Granted)
        {
            return new LocationResult("permission-denied");
        }

        if (await location.GetCurrentAsync(cancellationToken).ConfigureAwait(false) is not { } reading)
        {
            return new LocationResult("unavailable");
        }

        // Four decimal places is about 11 meters: enough to tell one customer site from another, and
        // no more precise than the job needs.
        return new LocationResult("located",
            Math.Round(reading.Latitude, 4), Math.Round(reading.Longitude, 4),
            reading.AccuracyMeters is { } accuracy ? Math.Round(accuracy) : null, reading.Timestamp);
    }

    public async Task<PhotoResult> TakePhotoAsync(
        [Description("The work order the photo is for, for example WO-1001.")] string workOrderId,
        [Description("What to photograph.")] PhotoSubject subject,
        CancellationToken cancellationToken)
    {
        if (!WorkOrderId().IsMatch(workOrderId))
        {
            return new PhotoResult("invalid");
        }

        if (!camera.IsSupported)
        {
            return new PhotoResult("unavailable");
        }

        // Every photo is asked for: the camera opening by itself would be a surprise, every time.
        var request = new ConsentRequest(DeviceCapability.Camera, "Open the camera for Beacon?",
            $"Beacon asked for a photo of the {Describe(subject)} for {workOrderId}.", OfferSessionAllowance: false);
        if (!await consent.AllowsAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return new PhotoResult("declined");
        }

        if (await permissions.EnsureAsync(DeviceCapability.Camera, cancellationToken).ConfigureAwait(false) is not PermissionOutcome.Granted)
        {
            return new PhotoResult("permission-denied");
        }

        if (await camera.CaptureAsync(cancellationToken).ConfigureAwait(false) is not { } photo)
        {
            return new PhotoResult("cancelled");
        }

        var attachment = attachments.Add(workOrderId, Describe(subject), photo);
        return new PhotoResult("captured", attachment.PhotoId, workOrderId, photo.SizeBytes);
    }

    public async Task<PartLabelResult> ScanPartLabelAsync(
        [Description("The work order the part is for, if there is one, for example WO-1001.")] string? workOrderId = null,
        CancellationToken cancellationToken = default)
    {
        // Reading a label is the technician's own act (she types or scans it), so it needs no separate
        // consent. What comes back is checked here: only a well-formed part number leaves the device.
        var job = workOrderId is not null && WorkOrderId().IsMatch(workOrderId) ? workOrderId : null;
        if (await labels.ReadAsync(new LabelReadRequest(job), cancellationToken).ConfigureAwait(false) is not { } reading)
        {
            return new PartLabelResult("cancelled");
        }

        return PartNumbers.TryNormalize(reading.Text, out var sku)
            ? new PartLabelResult("read", sku, reading.Method)
            : new PartLabelResult("unreadable");
    }

    private static string Describe(PhotoSubject subject) => subject switch
    {
        PhotoSubject.Nameplate => "nameplate",
        PhotoSubject.PartLabel => "part label",
        PhotoSubject.Damage => "damage",
        _ => "installation",
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        // Microsoft.Extensions.AI's defaults indent JSON; a result goes over the wire, so keep it compact.
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
        options.TypeInfoResolverChain.Insert(0, FieldToolsJsonContext.Default);
        options.MakeReadOnly();
        return options;
    }

    [GeneratedRegex(@"^WO-\d{4}$")]
    private static partial Regex WorkOrderId();
}
