namespace Ch10.Field.Tools;

/// <summary>A device feature a tool needs the technician's and the operating system's permission for.</summary>
public enum DeviceCapability
{
    Location,
    Camera,
}

/// <summary>What the operating system says about a permission once it has been checked (and asked for, if undecided).</summary>
public enum PermissionOutcome
{
    Granted,
    Denied,

    /// <summary>The device has no such feature, or the platform doesn't support it.</summary>
    NotSupported,
}

/// <summary>The operating system's permissions: check, and ask when the technician hasn't decided yet.</summary>
public interface IDevicePermissions
{
    Task<PermissionOutcome> EnsureAsync(DeviceCapability capability, CancellationToken cancellationToken);
}

/// <summary>The technician's answer to Beacon asking to use a device feature.</summary>
public enum ConsentDecision
{
    Deny,
    AllowOnce,

    /// <summary>Allow it until the app restarts or the technician revokes it.</summary>
    AllowForSession,
}

/// <summary>Beacon's request, in the technician's language: what it wants, and why.</summary>
/// <param name="Capability">The device feature.</param>
/// <param name="Title">A short question.</param>
/// <param name="Reason">Why Beacon wants it, built by the app from validated arguments.</param>
/// <param name="OfferSessionAllowance">Whether "allow for this session" is offered, or each use asks again.</param>
public sealed record ConsentRequest(DeviceCapability Capability, string Title, string Reason, bool OfferSessionAllowance);

/// <summary>Asks the technician, in the app's own UI, before Beacon uses a device feature.</summary>
public interface IConsentPrompt
{
    Task<ConsentDecision> AskAsync(ConsentRequest request, CancellationToken cancellationToken);
}

/// <summary>A position reading.</summary>
public sealed record DeviceLocation(double Latitude, double Longitude, double? AccuracyMeters, DateTimeOffset Timestamp);

/// <summary>The device's position.</summary>
public interface ILocationSource
{
    /// <summary>Reads the current position, or returns <see langword="null"/> when the device can't determine it.</summary>
    Task<DeviceLocation?> GetCurrentAsync(CancellationToken cancellationToken);
}

/// <summary>A photo saved on the device.</summary>
public sealed record CapturedPhoto(string FilePath, string ContentType, long SizeBytes);

/// <summary>The device's camera.</summary>
public interface ICameraCapture
{
    /// <summary>Whether this device can take a photo at all.</summary>
    bool IsSupported { get; }

    /// <summary>Opens the camera; returns <see langword="null"/> when the technician closed it without a photo.</summary>
    Task<CapturedPhoto?> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>What the technician is asked to read, and for which job.</summary>
public sealed record LabelReadRequest(string? WorkOrderId);

/// <summary>Text read from a part label, and how it was read (<c>manual</c> or <c>scanner</c>).</summary>
public sealed record LabelReading(string Text, string Method);

/// <summary>
/// Reads the part number on a label. .NET MAUI has no barcode API, so Beacon Field's implementation asks
/// the technician to type it; a platform barcode reader can implement this interface without touching the tool.
/// </summary>
public interface IPartLabelReader
{
    /// <summary>Reads a label; returns <see langword="null"/> when the technician cancelled.</summary>
    Task<LabelReading?> ReadAsync(LabelReadRequest request, CancellationToken cancellationToken);
}
