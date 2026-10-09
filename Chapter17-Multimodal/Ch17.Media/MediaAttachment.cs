using Microsoft.Extensions.AI;

namespace Ch17.Media;

/// <summary>A file the user picked, read and checked, ready to send; or the reason it can't be.</summary>
/// <param name="Content">The attachment, with the media type its bytes prove, or <see langword="null"/>.</param>
/// <param name="Problem">What's wrong with the file, phrased for the user, or <see langword="null"/>.</param>
public sealed record AttachmentResult(DataContent? Content, string? Problem);

/// <summary>
/// Turns a file into an attachment the way Beacon's clients do: reject what the agent would refuse before
/// uploading it, label it with the type its bytes prove rather than the extension or the browser's guess,
/// and strip a photo's metadata.
/// </summary>
public static class MediaAttachment
{
    /// <summary>Reads at most the policy's largest file size plus one chunk, then checks what was read.</summary>
    /// <param name="stream">The file's contents.</param>
    /// <param name="fileName">The name to show; it travels as <c>metadata.filename</c>.</param>
    /// <param name="size">The size the platform reports, if known, to refuse a huge file without reading it.</param>
    /// <param name="policy">Beacon's media policy.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<AttachmentResult> ReadAsync(
        Stream stream, string? fileName, long? size, MediaPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(policy);

        var name = SafeName(fileName);
        if (size > policy.LargestFile)
        {
            return new(null, $"{name} is {ByteSize.Format(size.Value)}. Beacon accepts {policy.Describe()}.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > policy.LargestFile)
            {
                return new(null, $"{name} is over {ByteSize.Format(policy.LargestFile)}. Beacon accepts {policy.Describe()}.");
            }
        }

        // The type comes from the bytes. A browser's file.ContentType or a file extension is only a hint.
        var bytes = buffer.ToArray();
        var detected = MediaSniffer.Detect(bytes);
        if (policy.Find(detected) is not { } rule)
        {
            return new(null, $"{name} is {MediaTypes.Describe(detected)}. Beacon accepts {policy.Describe()}.");
        }

        if (bytes.Length > rule.MaxBytes)
        {
            return new(null, $"{name} is {ByteSize.Format(bytes.Length)}; a {rule.Label} can be up to {ByteSize.Format(rule.MaxBytes)}.");
        }

        // Every photo loses its metadata before it leaves the device, whatever its size: Exif holds the location.
        return new(new DataContent(MediaMetadata.Strip(bytes, rule.MediaType), rule.MediaType) { Name = name }, null);
    }

    /// <summary>The media bytes a history carries. Every later turn sends them again.</summary>
    public static long BytesIn(IEnumerable<AIContent> contents) =>
        contents.OfType<DataContent>().Sum(d => (long)d.Data.Length);

    /// <summary>The file name without any path, trimmed to 80 characters.</summary>
    public static string SafeName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/')).Trim();
        return name.Length switch
        {
            0 => "attachment",
            > 80 => name[..80],
            _ => name,
        };
    }
}
