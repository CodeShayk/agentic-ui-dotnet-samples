using Ch19.Beacon.Adaptive.Planning;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;

namespace Ch19.Beacon.Field.Devices;

/// <summary>
/// Photos from the camera (<see cref="MediaPicker"/>) and voice notes picked as audio files the
/// technician recorded with the phone's recorder (<see cref="FilePicker"/>; MAUI has no recorder API).
/// The composer only calls this for inputs the plan offers.
/// </summary>
public sealed class EssentialsMediaCapture : IMediaCapture
{
    private static readonly FilePickerFileType AudioFiles = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.Android] = new[] { "audio/*" },
        [DevicePlatform.iOS] = new[] { "public.audio" },
        [DevicePlatform.WinUI] = new[] { ".wav", ".m4a", ".mp3" },
    });

    public async Task<MediaAttachment?> CaptureAsync(BeaconFeature feature, CancellationToken cancellationToken = default)
    {
        // The system UI must start on the main thread.
        var file = await MainThread.InvokeOnMainThreadAsync(() => feature switch
        {
            BeaconFeature.PhotoInput => MediaPicker.Default.CapturePhotoAsync(
                new MediaPickerOptions { Title = "Photo for Beacon", PreserveMetaData = false, MaximumWidth = 2048, MaximumHeight = 2048 }),
            BeaconFeature.VoiceNote => FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Choose a recording", FileTypes = AudioFiles }),
            _ => Task.FromResult<FileResult?>(null),
        });

        if (file is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await using (var source = await file.OpenReadAsync())
        {
            await source.CopyToAsync(buffer, cancellationToken);
        }

        return new MediaAttachment(file.FileName, file.ContentType ?? MediaTypeOf(file.FileName), buffer.ToArray());
    }

    private static string MediaTypeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        _ => "application/octet-stream",
    };
}
