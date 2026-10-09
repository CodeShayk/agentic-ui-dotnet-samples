using Ch17.Field.Capture;
using Ch17.Media;
using Microsoft.Extensions.AI;

namespace Ch17.Tests;

/// <summary>Beacon Field's capture flow over fakes: no camera, microphone or file system is touched.</summary>
public sealed class FieldCaptureTests
{
    private static PickedFile File(string name, byte[] bytes, long? size = null) =>
        new(name, size ?? bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes)));

    [Fact]
    public async Task A_photo_from_the_camera_is_checked_and_attached()
    {
        var (capture, tray, photos, _, _) = Rig(photo: File("IMG_0412.png", Samples.Nameplate));

        var status = await capture.AddPhotoAsync(TestContext.Current.CancellationToken);

        Assert.Equal("IMG_0412.png attached (56 KB).", status);
        Assert.Equal(1, photos.Captured);
        Assert.Equal("image/png", Assert.Single(tray.Items).MediaType);
    }

    [Fact]
    public async Task Without_a_camera_the_photo_library_is_used()
    {
        var (capture, tray, photos, _, _) = Rig(photo: File("plate.png", Samples.Nameplate), canCapture: false);

        await capture.AddPhotoAsync(TestContext.Current.CancellationToken);

        Assert.Equal((0, 1), (photos.Captured, photos.Picked));
        Assert.Single(tray.Items);
    }

    [Fact]
    public async Task A_denied_camera_says_what_to_do_and_opens_nothing()
    {
        var (capture, tray, photos, _, _) = Rig(photo: File("plate.png", Samples.Nameplate), access: DeviceAccess.Denied);

        var status = await capture.AddPhotoAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("Beacon Field can't use the camera.", status, StringComparison.Ordinal);
        Assert.Equal(0, photos.Captured);
        Assert.Empty(tray.Items);
    }

    [Fact]
    public async Task Recording_starts_then_stops_and_attaches_the_file()
    {
        var (capture, tray, _, recorder, _) = Rig(recording: File("noise-20261116-1104.wav", Samples.FanRecording));
        var ct = TestContext.Current.CancellationToken;

        Assert.StartsWith("Recording.", await capture.ToggleRecordingAsync(ct), StringComparison.Ordinal);
        Assert.True(capture.IsRecording);
        Assert.Equal("noise-20261116-1104.wav attached (157 KB).", await capture.ToggleRecordingAsync(ct));
        Assert.False(recorder.IsRecording);
        Assert.Equal("audio/wav", Assert.Single(tray.Items).MediaType);
    }

    [Fact]
    public async Task A_device_without_a_recorder_points_to_the_file_picker()
    {
        var (capture, _, _, recorder, _) = Rig(recorderSupported: false);

        var status = await capture.ToggleRecordingAsync(TestContext.Current.CancellationToken);

        Assert.Equal("This device can't record in Beacon Field. Record with another app, then attach the file with Attach file.", status);
        Assert.Equal(0, recorder.Started);
    }

    [Fact]
    public async Task A_file_is_labeled_by_its_bytes_and_a_disguised_one_never_leaves_the_phone()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, tray, _, _, _) = Rig();

        // A PDF saved with a .png name is a PDF; a program saved as .png is refused before upload.
        Assert.Equal("bulletin.png attached (2 KB).", await tray.AddAsync(File("bulletin.png", Samples.Bulletin), ct));
        Assert.Equal("application/pdf", tray.Items[0].MediaType);
        Assert.StartsWith("photo.png is a Windows program. Beacon accepts", await tray.AddAsync(File("photo.png", Samples.WindowsProgram), ct), StringComparison.Ordinal);
        Assert.StartsWith("clip.mp4 is an MP4 video.", await tray.AddAsync(File("clip.mp4", Samples.Mp4Video), ct), StringComparison.Ordinal);
        Assert.StartsWith("huge.png is 40.0 MB.", await tray.AddAsync(File("huge.png", [], size: 40 * ByteSize.MB), ct), StringComparison.Ordinal);
        Assert.Single(tray.Items);
    }

    [Fact]
    public async Task The_tray_holds_four_and_respects_the_conversation_budget()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, tray, _, _, _) = Rig();
        for (var i = 1; i <= 5; i++)
        {
            await tray.AddAsync(File($"plate-{i}.png", Samples.Nameplate), ct);
        }

        Assert.Equal(4, tray.Items.Count);

        var (refused, problem) = tray.TakeMessage("Four photos", conversationBytes: 20 * ByteSize.MB);
        Assert.Null(refused);
        Assert.StartsWith("This would bring the conversation to 20.2 MB of attachments", problem, StringComparison.Ordinal);

        var (message, _) = tray.TakeMessage("Four photos", conversationBytes: 0);
        Assert.Equal(["text", "image/png", "image/png", "image/png", "image/png"],
            message!.Contents.Select(c => c is DataContent d ? d.MediaType : "text"));
        Assert.Empty(tray.Items);
    }

    private static (FieldCapture, AttachmentTray, FakePhotos, FakeRecorder, FakeFiles) Rig(
        PickedFile? photo = null, PickedFile? recording = null, bool canCapture = true, bool recorderSupported = true,
        DeviceAccess access = DeviceAccess.Granted)
    {
        var tray = new AttachmentTray(MediaPolicy.Beacon);
        var photos = new FakePhotos(photo, canCapture, access);
        var recorder = new FakeRecorder(recording, recorderSupported);
        var files = new FakeFiles();
        return (new FieldCapture(photos, recorder, files, tray), tray, photos, recorder, files);
    }

    private sealed class FakePhotos(PickedFile? photo, bool canCapture, DeviceAccess access) : IPhotoSource
    {
        public int Captured { get; private set; }

        public int Picked { get; private set; }

        public bool CanCapture => canCapture;

        public Task<DeviceAccess> EnsureAccessAsync(CancellationToken cancellationToken) => Task.FromResult(access);

        public Task<PickedFile?> CaptureAsync(CancellationToken cancellationToken)
        {
            Captured++;
            return Task.FromResult(photo);
        }

        public Task<PickedFile?> PickAsync(CancellationToken cancellationToken)
        {
            Picked++;
            return Task.FromResult(photo);
        }
    }

    private sealed class FakeRecorder(PickedFile? recording, bool supported) : IAudioRecorder
    {
        public int Started { get; private set; }

        public bool IsSupported => supported;

        public bool IsRecording { get; private set; }

        public Task<DeviceAccess> EnsureAccessAsync(CancellationToken cancellationToken) => Task.FromResult(DeviceAccess.Granted);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started++;
            IsRecording = true;
            return Task.CompletedTask;
        }

        public Task<PickedFile?> StopAsync(CancellationToken cancellationToken)
        {
            IsRecording = false;
            return Task.FromResult(recording);
        }
    }

    private sealed class FakeFiles : IFileSource
    {
        public Task<PickedFile?> PickAsync(CancellationToken cancellationToken) => Task.FromResult<PickedFile?>(null);
    }
}
