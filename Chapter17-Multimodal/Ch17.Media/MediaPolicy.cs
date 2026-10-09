namespace Ch17.Media;

/// <summary>The kinds of media an AG-UI user message can carry.</summary>
public enum MediaKind
{
    Image,
    Audio,
    Document,
    Video,
}

/// <summary>One media type Beacon accepts, and how large it may be.</summary>
/// <param name="MediaType">The canonical media type.</param>
/// <param name="Kind">The AG-UI part it travels in.</param>
/// <param name="Label">How Beacon names it in messages, for example "PNG photo".</param>
/// <param name="MaxBytes">The largest file accepted, decoded.</param>
public sealed record MediaRule(string MediaType, MediaKind Kind, string Label, long MaxBytes);

/// <summary>
/// What Beacon accepts and how much of it. One instance is shared by the agent, which enforces it, and
/// the clients, which check the same rules before uploading anything.
/// </summary>
public sealed class MediaPolicy
{
    /// <summary>The media types accepted, each with its own size limit.</summary>
    public required IReadOnlyList<MediaRule> Accepted { get; init; }

    /// <summary>The most media parts one user message may carry.</summary>
    public int MaxPartsPerMessage { get; init; } = 4;

    /// <summary>
    /// The most media one request may carry, decoded, across the whole history. AG-UI clients resend
    /// the conversation every turn, so this bounds the conversation, not just the newest message.
    /// </summary>
    public long MaxBytesPerRequest { get; init; } = 20 * ByteSize.MB;

    /// <summary>Beacon's policy: photos, recordings and PDFs. No video.</summary>
    public static MediaPolicy Beacon { get; } = new()
    {
        Accepted =
        [
            new(MediaTypes.Png, MediaKind.Image, "PNG photo", 5 * ByteSize.MB),
            new(MediaTypes.Jpeg, MediaKind.Image, "JPEG photo", 5 * ByteSize.MB),
            new(MediaTypes.WebP, MediaKind.Image, "WebP photo", 5 * ByteSize.MB),
            new(MediaTypes.Wav, MediaKind.Audio, "WAV recording", 8 * ByteSize.MB),
            new(MediaTypes.Mp3, MediaKind.Audio, "MP3 recording", 8 * ByteSize.MB),
            new(MediaTypes.M4a, MediaKind.Audio, "M4A recording", 8 * ByteSize.MB),
            new(MediaTypes.Pdf, MediaKind.Document, "PDF document", 10 * ByteSize.MB),
        ],
    };

    /// <summary>The largest file of any accepted type.</summary>
    public long LargestFile => Accepted.Max(r => r.MaxBytes);

    /// <summary>The rule for a media type, or <see langword="null"/> if Beacon doesn't accept it.</summary>
    public MediaRule? Find(string? mediaType)
    {
        var normalized = MediaTypes.Normalize(mediaType);
        return Accepted.FirstOrDefault(r => r.MediaType == normalized);
    }

    /// <summary>"PNG, JPEG or WebP photos up to 5.0 MB, ...", for hints and refusals.</summary>
    public string Describe() =>
        "PNG, JPEG or WebP photos up to " + ByteSize.Format(MaxFor(MediaKind.Image)) +
        ", WAV, MP3 or M4A recordings up to " + ByteSize.Format(MaxFor(MediaKind.Audio)) +
        " and PDF documents up to " + ByteSize.Format(MaxFor(MediaKind.Document));

    /// <summary>The <c>accept</c> list for a file picker: every accepted media type.</summary>
    public string AcceptList => string.Join(',', Accepted.Select(r => r.MediaType));

    private long MaxFor(MediaKind kind) => Accepted.Where(r => r.Kind == kind).Select(r => r.MaxBytes).DefaultIfEmpty().Max();
}
