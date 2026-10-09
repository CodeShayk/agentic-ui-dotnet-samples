namespace Ch17.Media;

/// <summary>
/// Identifies a file from its first bytes ("magic numbers"). A file name, a browser's guess and a
/// declared <c>mimeType</c> are all claims; the bytes are what a decoder will actually read.
/// </summary>
public static class MediaSniffer
{
    /// <summary>How many leading bytes <see cref="Detect"/> needs to recognize every format it knows.</summary>
    public const int HeaderLength = 16;

    /// <summary>Returns the media type the bytes are, or <see langword="null"/> when they're no format Beacon knows.</summary>
    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return MediaTypes.Png;
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return MediaTypes.Jpeg;
        }

        if (IsRiff(data, "WEBP"u8))
        {
            return MediaTypes.WebP;
        }

        if (IsRiff(data, "WAVE"u8))
        {
            return MediaTypes.Wav;
        }

        if (data.StartsWith("%PDF-"u8))
        {
            return MediaTypes.Pdf;
        }

        if (data.StartsWith("ID3"u8) || IsMpegAudioFrame(data))
        {
            return MediaTypes.Mp3;
        }

        // ISO base media (MP4): "ftyp" at offset 4, then a brand. Only the M4A brand promises audio alone.
        if (data.Length >= 12 && data[4..8].SequenceEqual("ftyp"u8))
        {
            return data[8..12].SequenceEqual("M4A "u8) ? MediaTypes.M4a : MediaTypes.Mp4Video;
        }

        // Formats Beacon refuses, recognized so the refusal can say what the file really is.
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return MediaTypes.Gif;
        }

        if (data.StartsWith("MZ"u8))
        {
            return MediaTypes.WindowsProgram;
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[(byte)'P', (byte)'K', 3, 4]))
        {
            return MediaTypes.Zip;
        }

        return null;
    }

    private static bool IsRiff(ReadOnlySpan<byte> data, ReadOnlySpan<byte> form) =>
        data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual(form);

    // An MPEG audio frame header: 11 sync bits, a valid version and layer III.
    private static bool IsMpegAudioFrame(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0 && (data[1] & 0x18) != 0x08 && (data[1] & 0x06) == 0x02;
}
