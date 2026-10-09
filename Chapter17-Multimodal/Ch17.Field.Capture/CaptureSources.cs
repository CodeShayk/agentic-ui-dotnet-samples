namespace Ch17.Field.Capture;

/// <summary>A file the device produced or the technician picked, not yet read.</summary>
/// <param name="FileName">The name to show and send as <c>metadata.filename</c>.</param>
/// <param name="Size">The size the platform reports, when it knows.</param>
/// <param name="OpenAsync">Opens the file's contents.</param>
public sealed record PickedFile(string FileName, long? Size, Func<CancellationToken, Task<Stream>> OpenAsync);

/// <summary>Whether the operating system lets Beacon Field use a device.</summary>
public enum DeviceAccess
{
    Granted,
    Denied,
    NotSupported,
}

/// <summary>The camera, or the photo library when there's no camera.</summary>
public interface IPhotoSource
{
    /// <summary>Whether this device can take a photo (Windows needs a camera; an emulator may have none).</summary>
    bool CanCapture { get; }

    /// <summary>Asks the operating system for the camera, the first time.</summary>
    Task<DeviceAccess> EnsureAccessAsync(CancellationToken cancellationToken);

    /// <summary>Opens the camera. <see langword="null"/> when the technician cancels.</summary>
    Task<PickedFile?> CaptureAsync(CancellationToken cancellationToken);

    /// <summary>Opens the photo library. <see langword="null"/> when the technician cancels.</summary>
    Task<PickedFile?> PickAsync(CancellationToken cancellationToken);
}

/// <summary>The microphone. .NET MAUI has no recording API, so each platform supplies its own, or none.</summary>
public interface IAudioRecorder
{
    bool IsSupported { get; }

    bool IsRecording { get; }

    /// <summary>Asks the operating system for the microphone, the first time.</summary>
    Task<DeviceAccess> EnsureAccessAsync(CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops and returns the recording, or <see langword="null"/> if nothing was recorded.</summary>
    Task<PickedFile?> StopAsync(CancellationToken cancellationToken);
}

/// <summary>The file picker: recordings made with another app, PDFs, photos already on the device.</summary>
public interface IFileSource
{
    Task<PickedFile?> PickAsync(CancellationToken cancellationToken);
}
