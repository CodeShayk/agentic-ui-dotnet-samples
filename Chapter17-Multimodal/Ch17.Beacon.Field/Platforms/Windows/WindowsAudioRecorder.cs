using Ch17.Field.Capture;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Ch17.Beacon.Field.Devices;

/// <summary>
/// Records from the microphone on Windows with <c>Windows.Media.Capture</c>, since .NET MAUI has no
/// recording API. The profile is WAV at 16 kHz, mono, 16-bit: about 32 KB a second, so a ten-second
/// recording of a noise is around 320 KB, well inside Beacon's 8 MB limit for recordings.
/// </summary>
public sealed class WindowsAudioRecorder : IAudioRecorder, IDisposable
{
    private MediaCapture? _capture;
    private string? _path;

    public bool IsSupported => true;

    public bool IsRecording => _capture is not null;

    public Task<DeviceAccess> EnsureAccessAsync(CancellationToken cancellationToken) => DevicePermissions.EnsureAsync<Permissions.Microphone>();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Audio })
                .AsTask(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            var path = Path.Combine(FileSystem.CacheDirectory, $"noise-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            await File.WriteAllBytesAsync(path, [], cancellationToken);
            var file = await StorageFile.GetFileFromPathAsync(path);

            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            await capture.StartRecordToStorageFileAsync(profile, file)
                .AsTask(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            (_capture, _path) = (capture, path);
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    public async Task<PickedFile?> StopAsync(CancellationToken cancellationToken)
    {
        if (_capture is not { } capture || _path is not { } path)
        {
            return null;
        }

        try
        {
            await capture.StopRecordAsync().AsTask(cancellationToken);
        }
        finally
        {
            capture.Dispose();
            (_capture, _path) = (null, null);
        }

        var size = new FileInfo(path).Length;
        return size == 0 ? null : new PickedFile(Path.GetFileName(path), size, _ => Task.FromResult<Stream>(File.OpenRead(path)));
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _capture = null;
    }
}
