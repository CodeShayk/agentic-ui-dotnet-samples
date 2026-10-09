namespace Ch17.Media;

/// <summary>The media types Beacon knows, and the aliases clients send for them.</summary>
public static class MediaTypes
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string WebP = "image/webp";
    public const string Gif = "image/gif";
    public const string Wav = "audio/wav";
    public const string Mp3 = "audio/mpeg";
    public const string M4a = "audio/mp4";
    public const string Mp4Video = "video/mp4";
    public const string Pdf = "application/pdf";
    public const string Zip = "application/zip";
    public const string WindowsProgram = "application/x-msdownload";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpg"] = Jpeg,
        ["image/pjpeg"] = Jpeg,
        ["audio/x-wav"] = Wav,
        ["audio/wave"] = Wav,
        ["audio/vnd.wave"] = Wav,
        ["audio/mp3"] = Mp3,
        ["audio/x-m4a"] = M4a,
        ["audio/m4a"] = M4a,
    };

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        [Png] = "a PNG image",
        [Jpeg] = "a JPEG image",
        [WebP] = "a WebP image",
        [Gif] = "a GIF image",
        [Wav] = "a WAV recording",
        [Mp3] = "an MP3 recording",
        [M4a] = "an M4A recording",
        [Mp4Video] = "an MP4 video",
        [Pdf] = "a PDF document",
        [Zip] = "a ZIP archive",
        [WindowsProgram] = "a Windows program",
    };

    /// <summary>Lowercases, drops parameters (<c>; codecs=...</c>) and maps aliases to one name.</summary>
    public static string Normalize(string? mediaType)
    {
        var bare = (mediaType ?? string.Empty).Split(';', 2)[0].Trim().ToLowerInvariant();
        return Aliases.TryGetValue(bare, out var canonical) ? canonical : bare;
    }

    /// <summary>"a PNG image", for a type Beacon knows; a neutral phrase otherwise, so client text never leaks into messages.</summary>
    public static string Describe(string? mediaType) =>
        mediaType is not null && Descriptions.TryGetValue(Normalize(mediaType), out var description)
            ? description
            : "a type Beacon doesn't recognize";
}
