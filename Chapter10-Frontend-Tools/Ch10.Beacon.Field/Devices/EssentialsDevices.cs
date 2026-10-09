using Ch10.Field.Tools;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;

namespace Ch10.Beacon.Field.Devices;

/// <summary>The device's position, from .NET MAUI's <see cref="Geolocation"/>.</summary>
public sealed class EssentialsLocationSource : ILocationSource
{
    public async Task<DeviceLocation?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Medium accuracy is tens of meters, plenty to tell customer sites apart, and quicker and
            // kinder to the battery than High.
            var location = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(10)), cancellationToken);

            return location is null
                ? null
                : new DeviceLocation(location.Latitude, location.Longitude, location.Accuracy, location.Timestamp);
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or FeatureNotEnabledException or PermissionException)
        {
            return null; // no location hardware, location turned off, or permission withdrawn mid-request
        }
    }
}

/// <summary>The device's camera, from .NET MAUI's <see cref="MediaPicker"/>. Photos are copied into the app's own storage.</summary>
public sealed class EssentialsCameraCapture : ICameraCapture
{
    public bool IsSupported => MediaPicker.Default.IsCaptureSupported;

    public async Task<CapturedPhoto?> CaptureAsync(CancellationToken cancellationToken)
    {
        // The camera UI must start on the main thread. Location metadata stays out of the file
        // (where the platform supports dropping it): the photo is evidence of a nameplate, not of where
        // the technician stood.
        var file = await MainThread.InvokeOnMainThreadAsync(() => MediaPicker.Default.CapturePhotoAsync(
            new MediaPickerOptions { Title = "Photo for Beacon", PreserveMetaData = false, MaximumWidth = 2048, MaximumHeight = 2048 }));
        if (file is null)
        {
            return null;
        }

        var folder = Path.Combine(FileSystem.AppDataDirectory, "photos");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");

        await using (var source = await file.OpenReadAsync())
        await using (var target = File.Create(path))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        return new CapturedPhoto(path, file.ContentType ?? "image/jpeg", new FileInfo(path).Length);
    }
}

/// <summary>The operating system's permissions, from .NET MAUI's <see cref="Permissions"/>.</summary>
public sealed class EssentialsPermissions : IDevicePermissions
{
    public Task<PermissionOutcome> EnsureAsync(DeviceCapability capability, CancellationToken cancellationToken) => capability switch
    {
        DeviceCapability.Location => EnsureAsync<Permissions.LocationWhenInUse>(),
        DeviceCapability.Camera => EnsureAsync<Permissions.Camera>(),
        _ => Task.FromResult(PermissionOutcome.NotSupported),
    };

    // Check first, ask only when undecided, and on the main thread: the system dialog belongs to the UI.
    private static Task<PermissionOutcome> EnsureAsync<TPermission>() where TPermission : Permissions.BasePermission, new() =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                var status = await Permissions.CheckStatusAsync<TPermission>();
                if (status is not (PermissionStatus.Granted or PermissionStatus.Limited))
                {
                    status = await Permissions.RequestAsync<TPermission>();
                }

                return status is PermissionStatus.Granted or PermissionStatus.Limited ? PermissionOutcome.Granted : PermissionOutcome.Denied;
            }
            catch (FeatureNotSupportedException)
            {
                return PermissionOutcome.NotSupported;
            }
        });
}
