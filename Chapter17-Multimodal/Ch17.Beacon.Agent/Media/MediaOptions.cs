using Ch17.Media;

namespace Ch17.Beacon.Agent.Media;

/// <summary>Beacon's media settings, from the <c>Beacon:Media</c> configuration section.</summary>
public sealed class MediaOptions
{
    public const string SectionName = "Beacon:Media";

    /// <summary>The only hosts Beacon fetches URL sources from. Exact names, HTTPS only.</summary>
    public List<string> AllowedUrlHosts { get; set; } = [];

    /// <summary>How long one URL fetch may take, redirects included.</summary>
    public int FetchTimeoutSeconds { get; set; } = 10;

    /// <summary>How many redirects a URL source may follow; each hop is checked like the first.</summary>
    public int MaxRedirects { get; set; } = 3;

    /// <summary>A fixed request body limit, for tests. Leave it unset and <see cref="BodyLimit"/> derives one.</summary>
    public long? MaxRequestBodyBytes { get; set; }

    /// <summary>
    /// The largest request body Kestrel reads, derived from the attachment budget so a conversation inside the
    /// budget is never refused for size. Base64 turns 3 bytes into 4 characters, and System.Text.Json writes
    /// each '+' as a six-character escape, so Beacon's requests measured 1.46 times their attachments. One and
    /// a half times the budget, plus 1 MB for the text and the JSON around it: 31 MB for Beacon's 20 MB.
    /// </summary>
    public long BodyLimit(MediaPolicy policy) =>
        MaxRequestBodyBytes ?? policy.MaxBytesPerRequest * 3 / 2 + ByteSize.MB;
}
