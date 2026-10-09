using System.IO.Compression;
using System.Text;
using AGUI.Abstractions;
using Ch19.Beacon.Adaptive.Planning;

namespace Ch19.Tests.Infrastructure;

/// <summary>Media generated in code. No camera, no microphone.</summary>
public static class TestMedia
{
    /// <summary>A 16x16 solid-blue PNG.</summary>
    public static byte[] Png()
    {
        var raw = new MemoryStream();
        for (var y = 0; y < 16; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < 16; x++)
            {
                raw.Write([0x00, 0x78, 0xD4]);
            }
        }

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.WriteTo(zlib);
        }

        byte[] header = [.. BigEndian(16), .. BigEndian(16), 8, 2, 0, 0, 0];
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", header), .. Chunk("IDAT", compressed.ToArray()), .. Chunk("IEND", [])];
    }

    /// <summary>A 0.1-second 440 Hz tone: 8 kHz, 16-bit mono PCM WAV.</summary>
    public static byte[] Wav()
    {
        const int rate = 8000, samples = 800;
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / rate));
            pcm[2 * i] = (byte)value;
            pcm[(2 * i) + 1] = (byte)(value >> 8);
        }

        using var wav = new MemoryStream();
        using var writer = new BinaryWriter(wav);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return wav.ToArray();
    }

    public static MediaAttachment Photo => new("eq-501-nameplate.png", "image/png", Png());

    public static MediaAttachment Recording => new("eq-501-start.wav", "audio/wav", Wav());

    /// <summary>A user message with text and one inline media part, as a client would send it.</summary>
    public static AGUIUserMessage UserMessage(string id, string text, AGUIMediaInputContent part) => new()
    {
        Id = id,
        Content = new AGUIContent([new AGUITextInputContent { Text = text }, part]),
    };

    public static AGUIImageInputContent ImagePart(byte[]? bytes = null) => new()
    {
        Source = new AGUIInputContentDataSource { Value = Convert.ToBase64String(bytes ?? Png()), MimeType = "image/png" },
    };

    public static AGUIAudioInputContent AudioPart() => new()
    {
        Source = new AGUIInputContentDataSource { Value = Convert.ToBase64String(Wav()), MimeType = "audio/wav" },
    };

    private static byte[] Chunk(string type, byte[] data)
    {
        var name = Encoding.ASCII.GetBytes(type);
        return [.. BigEndian((uint)data.Length), .. name, .. data, .. BigEndian(Crc32([.. name, .. data]))];
    }

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}

/// <summary>An <see cref="IMediaCapture"/> that returns prepared media and counts what was asked of it.</summary>
public sealed class FakeMediaCapture(params MediaAttachment[] media) : IMediaCapture
{
    private readonly Queue<MediaAttachment> _media = new(media);

    public List<BeaconFeature> Requests { get; } = [];

    public Task<MediaAttachment?> CaptureAsync(BeaconFeature feature, CancellationToken cancellationToken = default)
    {
        Requests.Add(feature);
        return Task.FromResult(_media.TryDequeue(out var next) ? next : null);
    }
}
