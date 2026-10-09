using System.Buffers.Binary;

namespace Ch17.Media;

/// <summary>
/// Removes the metadata cameras and editors write into photos: Exif, which holds where and when the photo
/// was taken and on what device, plus XMP, IPTC, text chunks and comments. It walks the file's own structure
/// and drops whole segments or chunks, so the pixels are untouched and nothing is decoded or re-encoded.
/// </summary>
/// <remarks>
/// The orientation flag lives in Exif too, so a photo the camera stored sideways reaches the model sideways.
/// When a file's structure can't be walked to its end (a damaged file), the part that couldn't be walked is
/// kept as it is.
/// </remarks>
public static class MediaMetadata
{
    /// <summary>The photo without its metadata, or <paramref name="data"/> itself when it has none or isn't a photo.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="mediaType">The type its bytes were proven to be.</param>
    public static ReadOnlyMemory<byte> Strip(ReadOnlyMemory<byte> data, string? mediaType)
    {
        var type = MediaTypes.Normalize(mediaType);
        if (type is not (MediaTypes.Jpeg or MediaTypes.Png or MediaTypes.WebP) || MediaSniffer.Detect(data.Span) != type)
        {
            return data;
        }

        var output = new Output(data.Length);
        switch (type)
        {
            case MediaTypes.Jpeg:
                StripJpeg(data.Span, output);
                break;
            case MediaTypes.Png:
                StripPng(data.Span, output);
                break;
            default:
                StripWebP(data.Span, output);
                break;
        }

        // Stripping only ever removes bytes, so the same length means nothing was removed.
        return output.Length < data.Length ? output.Written : data;
    }

    // JPEG: marker segments up to the image data. Keep APP0 (JFIF), an APP2 that holds the ICC color profile
    // and APP14 (Adobe's color transform), which decoders need. Drop APP1 (Exif and XMP), every other APPn
    // segment (IPTC, multi-picture data, vendor notes) and comments. Phones append further images after the
    // end-of-image marker, each with its own Exif, so nothing after that marker is kept.
    private static void StripJpeg(ReadOnlySpan<byte> data, Output output)
    {
        output.Write(data[..2]); // start of image
        var i = 2;
        while (i < data.Length)
        {
            if (data[i] != 0xFF)
            {
                // Entropy-coded image data after a scan header, up to the next marker.
                var next = NextMarker(data, i);
                output.Write(data[i..next]);
                i = next;
                continue;
            }

            if (i + 1 >= data.Length)
            {
                break;
            }

            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++; // a fill byte before a marker
                continue;
            }

            if (marker == 0xD9)
            {
                output.Write(data.Slice(i, 2)); // end of image
                return;
            }

            if (marker is 0x00 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                output.Write(data.Slice(i, 2)); // a stuffed byte, TEM or a restart marker: no length follows
                i += 2;
                continue;
            }

            if (i + 4 > data.Length)
            {
                break;
            }

            var end = i + 2 + BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
            if (end < i + 4 || end > data.Length)
            {
                break;
            }

            if (!IsJpegMetadata(marker, data[(i + 4)..end]))
            {
                output.Write(data[i..end]);
            }

            i = end;
        }

        output.Write(data[i..]); // what couldn't be walked stays as it is
    }

    private static bool IsJpegMetadata(byte marker, ReadOnlySpan<byte> payload) => marker switch
    {
        0xE0 or 0xEE => false,
        0xE2 => !payload.StartsWith("ICC_PROFILE\0"u8),
        >= 0xE1 and <= 0xEF => true,
        0xFE => true,
        _ => false,
    };

    // In entropy-coded data, 0xFF is followed by 0x00 (a stuffed byte) or a restart marker; anything else is a marker.
    private static int NextMarker(ReadOnlySpan<byte> data, int start)
    {
        for (var j = start; j + 1 < data.Length; j++)
        {
            if (data[j] == 0xFF && data[j + 1] != 0x00 && data[j + 1] is not (>= 0xD0 and <= 0xD7))
            {
                return j;
            }
        }

        return data.Length;
    }

    // PNG: chunks after the 8-byte signature. Drop eXIf, the text chunks (tEXt, zTXt and iTXt, where editors
    // also put XMP) and tIME, and keep nothing after IEND.
    private static void StripPng(ReadOnlySpan<byte> data, Output output)
    {
        output.Write(data[..8]);
        var i = 8;
        while (i + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
            if (length > (uint)(data.Length - i - 12))
            {
                break;
            }

            var end = i + 12 + (int)length;
            var type = data.Slice(i + 4, 4);
            if (!(type.SequenceEqual("eXIf"u8) || type.SequenceEqual("tEXt"u8) || type.SequenceEqual("zTXt"u8)
                || type.SequenceEqual("iTXt"u8) || type.SequenceEqual("tIME"u8)))
            {
                output.Write(data[i..end]);
            }

            i = end;
            if (type.SequenceEqual("IEND"u8))
            {
                return;
            }
        }

        output.Write(data[i..]);
    }

    // WebP: RIFF chunks after "RIFF", the size and "WEBP". Drop the EXIF and XMP chunks, clear their flags in
    // the VP8X header and write the new RIFF size.
    private static void StripWebP(ReadOnlySpan<byte> data, Output output)
    {
        output.Write(data[..12]);
        var i = 12;
        while (i + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            var padded = size + (size & 1);
            if (padded > (uint)(data.Length - i - 8))
            {
                break;
            }

            var end = i + 8 + (int)padded;
            var fourCc = data.Slice(i, 4);
            if (!(fourCc.SequenceEqual("EXIF"u8) || fourCc.SequenceEqual("XMP "u8)))
            {
                var at = output.Length;
                output.Write(data[i..end]);
                if (fourCc.SequenceEqual("VP8X"u8) && size >= 1)
                {
                    output.Buffer[at + 8] &= unchecked((byte)~(0x08 | 0x04)); // the Exif and XMP flags
                }
            }

            i = end;
        }

        output.Write(data[i..]);
        BinaryPrimitives.WriteUInt32LittleEndian(output.Buffer.AsSpan(4), (uint)(output.Length - 8));
    }

    // Stripping never grows a file, so one buffer the size of the input is enough.
    private sealed class Output(int capacity)
    {
        public byte[] Buffer { get; } = new byte[capacity];

        public int Length { get; private set; }

        public ReadOnlyMemory<byte> Written => Buffer.AsMemory(0, Length);

        public void Write(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(Buffer.AsSpan(Length));
            Length += bytes.Length;
        }
    }
}
