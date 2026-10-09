using Ch17.Field.Capture;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;

namespace Ch17.Beacon.Field.Devices;

/// <summary>Photos from .NET MAUI's <see cref="MediaPicker"/>: the camera, or the library when there's no camera.</summary>
public sealed class EssentialsPhotoSource : IPhotoSource
{
    // Resized and recompressed on the device: a nameplate needs legible text, not twelve megapixels.
    // Location metadata is dropped where the platform supports it (not yet on Windows).
    private static readonly MediaPickerOptions Options = new()
    {
        Title = "Photo for Beacon",
        MaximumWidth = 2048,
        MaximumHeight = 2048,
        CompressionQuality = 85,
        RotateImage = true,
        PreserveMetaData = false,
    };

    public bool CanCapture => MediaPicker.Default.IsCaptureSupported;

    public Task<DeviceAccess> EnsureAccessAsync(CancellationToken cancellationToken) => DevicePermissions.EnsureAsync<Permissions.Camera>();

    public async Task<PickedFile?> CaptureAsync(CancellationToken cancellationToken) =>
        ToPickedFile(await MainThread.InvokeOnMainThreadAsync(() => MediaPicker.Default.CapturePhotoAsync(Options)));

    public async Task<PickedFile?> PickAsync(CancellationToken cancellationToken) =>
        ToPickedFile((await MainThread.InvokeOnMainThreadAsync(() => MediaPicker.Default.PickPhotosAsync(Options))).FirstOrDefault());

    internal static PickedFile? ToPickedFile(FileResult? result) =>
        result is null ? null : new PickedFile(result.FileName, null, _ => result.OpenReadAsync());
}

/// <summary>Any file Beacon accepts, from .NET MAUI's <see cref="FilePicker"/>.</summary>
public sealed class EssentialsFileSource : IFileSource
{
    // The picker filters by platform-specific types; Ch17.Media still checks the bytes afterwards.
    private static readonly FilePickerFileType BeaconFiles = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.WinUI] = (string[])[".png", ".jpg", ".jpeg", ".webp", ".wav", ".mp3", ".m4a", ".pdf"],
        [DevicePlatform.Android] = (string[])["image/png", "image/jpeg", "image/webp", "audio/wav", "audio/x-wav", "audio/mpeg", "audio/mp4", "application/pdf"],
        [DevicePlatform.iOS] = (string[])["public.png", "public.jpeg", "org.webmproject.webp", "com.microsoft.waveform-audio", "public.mp3", "com.apple.m4a-audio", "com.adobe.pdf"],
    });

    public async Task<PickedFile?> PickAsync(CancellationToken cancellationToken) =>
        EssentialsPhotoSource.ToPickedFile(await MainThread.InvokeOnMainThreadAsync(() =>
            FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Attach a photo, recording or PDF", FileTypes = BeaconFiles })));
}

/// <summary>Platforms without a recorder here: the technician records with another app and attaches the file.</summary>
public sealed class NoAudioRecorder : IAudioRecorder
{
    public bool IsSupported => false;

    public bool IsRecording => false;

    public Task<DeviceAccess> EnsureAccessAsync(CancellationToken cancellationToken) => Task.FromResult(DeviceAccess.NotSupported);

    public Task StartAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PickedFile?> StopAsync(CancellationToken cancellationToken) => Task.FromResult<PickedFile?>(null);
}

/// <summary>The operating system's permissions, checked first and requested only when undecided, on the main thread.</summary>
internal static class DevicePermissions
{
    public static Task<DeviceAccess> EnsureAsync<TPermission>() where TPermission : Permissions.BasePermission, new() =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                var status = await Permissions.CheckStatusAsync<TPermission>();
                if (status is not (PermissionStatus.Granted or PermissionStatus.Limited))
                {
                    status = await Permissions.RequestAsync<TPermission>();
                }

                return status is PermissionStatus.Granted or PermissionStatus.Limited ? DeviceAccess.Granted : DeviceAccess.Denied;
            }
            catch (FeatureNotSupportedException)
            {
                return DeviceAccess.NotSupported;
            }
        });
}
