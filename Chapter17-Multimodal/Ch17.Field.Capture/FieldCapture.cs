namespace Ch17.Field.Capture;

/// <summary>
/// Beacon Field's three ways to attach something: take a photo, record the noise or pick a file. The
/// technician starts each one by tapping a button, so that tap is the consent; the operating system's
/// permission is asked next, and a device that can't do something falls back to the file picker.
/// </summary>
public sealed class FieldCapture(IPhotoSource photos, IAudioRecorder recorder, IFileSource files, AttachmentTray tray)
{
    public bool IsRecording => recorder.IsRecording;

    public bool CanRecord => recorder.IsSupported;

    /// <summary>The camera when there is one, the photo library when there isn't.</summary>
    public async Task<string> AddPhotoAsync(CancellationToken cancellationToken)
    {
        PickedFile? photo;
        if (photos.CanCapture)
        {
            if (await photos.EnsureAccessAsync(cancellationToken).ConfigureAwait(false) is not DeviceAccess.Granted)
            {
                return "Beacon Field can't use the camera. Allow it in Settings, or pick a photo with Attach file.";
            }

            photo = await photos.CaptureAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            photo = await photos.PickAsync(cancellationToken).ConfigureAwait(false);
        }

        return photo is null ? "No photo was added." : await tray.AddAsync(photo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts a recording, or stops the one in progress and attaches it.</summary>
    public async Task<string> ToggleRecordingAsync(CancellationToken cancellationToken)
    {
        if (!recorder.IsSupported)
        {
            return "This device can't record in Beacon Field. Record with another app, then attach the file with Attach file.";
        }

        if (recorder.IsRecording)
        {
            var recording = await recorder.StopAsync(cancellationToken).ConfigureAwait(false);
            return recording is null ? "Nothing was recorded." : await tray.AddAsync(recording, cancellationToken).ConfigureAwait(false);
        }

        if (await recorder.EnsureAccessAsync(cancellationToken).ConfigureAwait(false) is not DeviceAccess.Granted)
        {
            return "Beacon Field can't use the microphone. Allow it in Settings, or attach a recording with Attach file.";
        }

        await recorder.StartAsync(cancellationToken).ConfigureAwait(false);
        return "Recording. Hold the phone near the noise, then tap Stop recording.";
    }

    /// <summary>Any file: a recording from another app, a PDF, a photo already on the device.</summary>
    public async Task<string> AddFileAsync(CancellationToken cancellationToken)
    {
        var file = await files.PickAsync(cancellationToken).ConfigureAwait(false);
        return file is null ? "No file was added." : await tray.AddAsync(file, cancellationToken).ConfigureAwait(false);
    }
}
