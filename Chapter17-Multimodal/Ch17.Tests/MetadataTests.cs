using System.Buffers.Binary;
using System.Text;
using Beacon.Testing;
using Ch17.Media;
using Microsoft.Extensions.AI;

namespace Ch17.Tests;

/// <summary>
/// Location metadata never reaches the model: every photo loses its Exif, XMP, text chunks and comments, on
/// the client before upload and again on the server, whatever its size, inline or fetched.
/// </summary>
public sealed class MetadataTests
{
    private static readonly byte[] Gps = "GPS 43.6532N 70.2553W"u8.ToArray();

    [Fact]
    public void A_JPEG_keeps_its_image_segments_and_loses_Exif_IPTC_comments_and_appended_images()
    {
        var jfif = Segment(0xE0, [.. "JFIF\0"u8, 1, 2, 0, 0, 1, 0, 1, 0, 0]);
        var icc = Segment(0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, 0xAA]);
        var quantization = Segment(0xDB, [0, .. new byte[64]]);
        var scan = Segment(0xDA, [1, 1, 0, 0, 63, 0]);
        byte[] entropy = [0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56]; // a stuffed byte and a restart marker
        byte[] start = [0xFF, 0xD8], end = [0xFF, 0xD9];

        byte[] photo =
        [
            .. start, .. jfif,
            .. Segment(0xE1, [.. "Exif\0\0"u8, .. Gps]),                // Exif: location, time, device
            .. Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. Gps]), // XMP
            .. icc,
            .. Segment(0xE2, [.. "MPF\0"u8, 0, 0]),                    // multi-picture index
            .. Segment(0xED, [.. "Photoshop 3.0\0"u8, .. Gps]),        // IPTC
            .. Segment(0xFE, [.. "taken at Maple Grove"u8]),           // comment
            .. quantization, .. scan, .. entropy, .. end,
            .. start, .. Segment(0xE1, [.. "Exif\0\0"u8, .. Gps]), .. end, // a second image a phone appended
        ];

        var stripped = MediaMetadata.Strip(photo, MediaTypes.Jpeg).ToArray();

        Assert.Equal([.. start, .. jfif, .. icc, .. quantization, .. scan, .. entropy, .. end], stripped);
    }

    [Fact]
    public void A_PNG_keeps_its_image_chunks_and_loses_eXIf_text_and_time()
    {
        var header = Chunk("IHDR", new byte[13]);
        var data = Chunk("IDAT", [1, 2, 3, 4]);
        var last = Chunk("IEND", []);
        byte[] signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

        byte[] photo =
        [
            .. signature, .. header,
            .. Chunk("eXIf", Gps), .. Chunk("tEXt", [.. "Location\0"u8, .. Gps]), .. Chunk("iTXt", [.. "XML:com.adobe.xmp\0"u8, .. Gps]),
            .. Chunk("zTXt", [.. "Comment\0"u8, 0, 0x78]), .. Chunk("tIME", [7, 234, 11, 16, 9, 0, 0]),
            .. data, .. last, .. Gps,
        ];

        var stripped = MediaMetadata.Strip(photo, MediaTypes.Png).ToArray();

        Assert.Equal([.. signature, .. header, .. data, .. last], stripped);
    }

    [Fact]
    public void A_WebP_loses_its_EXIF_and_XMP_chunks_and_their_flags()
    {
        var extended = RiffChunk("VP8X", [0x2C, 0, 0, 0, 9, 0, 0, 9, 0, 0]); // ICC, Exif and XMP flags set
        var profile = RiffChunk("ICCP", [1, 2, 3, 4]);
        var image = RiffChunk("VP8 ", [1, 2, 3]); // odd size: one padding byte follows

        var photo = Riff([.. extended, .. profile, .. image, .. RiffChunk("EXIF", Gps), .. RiffChunk("XMP ", Gps)]);
        var stripped = MediaMetadata.Strip(photo, MediaTypes.WebP).ToArray();

        extended[8] = 0x20; // only the ICC flag is left
        Assert.Equal(Riff([.. extended, .. profile, .. image]), stripped);
    }

    [Fact]
    public void Files_without_metadata_and_files_that_are_not_photos_come_back_untouched()
    {
        ReadOnlyMemory<byte> nameplate = Samples.Nameplate, recording = Samples.FanRecording;

        Assert.True(MediaMetadata.Strip(nameplate, MediaTypes.Png).Equals(nameplate)); // the same memory, not a copy
        Assert.True(MediaMetadata.Strip(recording, MediaTypes.Wav).Equals(recording));
        Assert.True(MediaMetadata.Strip(nameplate, MediaTypes.Jpeg).Equals(nameplate)); // the bytes must match the type
    }

    [Fact]
    public void A_damaged_segment_and_what_follows_it_are_kept_as_they_are()
    {
        byte[] photo = [0xFF, 0xD8, .. Segment(0xFE, [.. "note"u8]), 0xFF, 0xE1, 0x7F, 0xFF, .. Gps]; // APP1 claims 32 KB

        var stripped = MediaMetadata.Strip(photo, MediaTypes.Jpeg).ToArray();

        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE1, 0x7F, 0xFF, .. Gps], stripped);
    }

    [Fact]
    public async Task The_clients_strip_a_small_photo_before_it_leaves_the_device()
    {
        var photo = JpegWithLocation();
        Assert.True(photo.Length < 2 * ByteSize.MB); // under the Desk's resize threshold, so nothing re-encodes it

        var result = await MediaAttachment.ReadAsync(new MemoryStream(photo), "plate.jpg", photo.Length, MediaPolicy.Beacon,
            TestContext.Current.CancellationToken);

        Assert.False(Contains(result.Content!.Data.Span, Gps));
        Assert.Equal(MediaTypes.Jpeg, result.Content.MediaType);
    }

    [Fact]
    public async Task The_agent_strips_every_photo_it_passes_on_inline_or_fetched()
    {
        var ct = TestContext.Current.CancellationToken;
        const string url = "https://media.contoso-climate.example/wo-1003/plate.jpg";
        var model = new RecordedChatClient(RecordedChatClient.TextTurn("msg-1", "Got both."));
        var server = new FakeMediaServer().Serve(url, JpegWithLocation(), "image/jpeg");
        await using var agent = new AgentHost(model, services: AgentHost.FakeInternet(server, FakeDns.Public));

        // A client that didn't strip anything: the photo inline, and the same photo by URL.
        await AGUIWire.RunRawAsync(agent.CreateClient(), "/agent",
            Runs.User(Runs.Text("Two photos."), Runs.Image(JpegWithLocation(), "image/jpeg"), Runs.ImageUrl(url)), ct);

        var photos = model.Calls.Single().Messages.Single().Contents.OfType<DataContent>().ToList();
        Assert.Equal(["attachment-1", "attachment-2"], photos.Select(p => p.Name));
        Assert.All(photos, p => Assert.False(Contains(p.Data.Span, Gps)));
        Assert.All(photos, p => Assert.Equal(MediaTypes.Jpeg, MediaSniffer.Detect(p.Data.Span)));
    }

    private static byte[] JpegWithLocation() =>
    [
        0xFF, 0xD8, .. Segment(0xE0, [.. "JFIF\0"u8, 1, 2, 0, 0, 1, 0, 1, 0, 0]), .. Segment(0xE1, [.. "Exif\0\0"u8, .. Gps]),
        .. Segment(0xDA, [1, 1, 0, 0, 63, 0]), .. new byte[4096], 0xFF, 0xD9,
    ];

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var segment = new byte[4 + payload.Length];
        (segment[0], segment[1]) = (0xFF, marker);
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return segment;
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length]; // the CRC stays zero: stripping never checks it
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] RiffChunk(string fourCc, byte[] data)
    {
        var chunk = new byte[8 + data.Length + (data.Length & 1)];
        Encoding.ASCII.GetBytes(fourCc).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)data.Length);
        data.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] Riff(byte[] chunks)
    {
        byte[] file = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. chunks];
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(file.Length - 8));
        return file;
    }

    private static bool Contains(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value) => data.IndexOf(value) >= 0;
}
