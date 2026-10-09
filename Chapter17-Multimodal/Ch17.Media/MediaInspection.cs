namespace Ch17.Media;

/// <summary>Why a media part wasn't accepted. The codes go on the wire; the reasons are Beacon's own words.</summary>
public enum MediaRefusalCode
{
    /// <summary>A media type Beacon doesn't take (video, GIF, archives, ...).</summary>
    NotAccepted,

    /// <summary>The bytes aren't what the part says they are.</summary>
    TypeMismatch,

    /// <summary>Over the size limit for its type.</summary>
    TooLarge,

    /// <summary>A URL source Beacon refused to fetch, or couldn't.</summary>
    UrlRefused,

    /// <summary>A provider file handle Beacon can't use.</summary>
    ProviderFile,
}

/// <summary>The outcome of checking one media part: accepted with its rule, or refused with a reason.</summary>
public sealed record MediaVerdict(MediaRule? Rule, MediaRefusalCode? Code, string? Reason)
{
    public bool Accepted => Rule is not null;

    public static MediaVerdict Accept(MediaRule rule) => new(rule, null, null);

    public static MediaVerdict Refuse(MediaRefusalCode code, string reason) => new(null, code, reason);
}

/// <summary>
/// The check every media part passes before Beacon uses it: an accepted type, within its size limit,
/// whose bytes are what it claims. Refusal reasons are built only from values Beacon knows (its own
/// labels, sizes and the sniffed format), never from the file name or the declared type a client sent,
/// because the reason is shown to the model.
/// </summary>
public static class MediaInspection
{
    public static MediaVerdict Inspect(string? declaredMediaType, ReadOnlySpan<byte> data, MediaPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // 1. A type Beacon takes at all. Video, GIFs and archives stop here, whatever their bytes are.
        if (policy.Find(declaredMediaType) is not { } rule)
        {
            return MediaVerdict.Refuse(MediaRefusalCode.NotAccepted,
                $"it is labeled as {MediaTypes.Describe(declaredMediaType)}, and Beacon accepts only {policy.Describe()}");
        }

        // 2. Within that type's limit, measured on the decoded bytes.
        if (data.Length > rule.MaxBytes)
        {
            return MediaVerdict.Refuse(MediaRefusalCode.TooLarge,
                $"the {rule.Label} is {ByteSize.Format(data.Length)}, over Beacon's {ByteSize.Format(rule.MaxBytes)} limit");
        }

        // 3. The bytes are what the label says. A program renamed to .png fails here.
        var actual = MediaSniffer.Detect(data);
        if (actual != rule.MediaType)
        {
            return MediaVerdict.Refuse(MediaRefusalCode.TypeMismatch,
                $"it is labeled as {MediaTypes.Describe(rule.MediaType)}, but its bytes are " +
                (actual is null ? "not a format Beacon recognizes" : MediaTypes.Describe(actual)));
        }

        return MediaVerdict.Accept(rule);
    }
}
