using System.Buffers.Text;
using System.Net.Http.Headers;
using AGUI.Abstractions;
using Ch17.Media;

namespace Ch17.Beacon.Agent.Media;

/// <summary>
/// The checks that run on <see cref="RunAgentInput"/> before the run starts, reporting each problem by its
/// JSON path: base64 that doesn't decode, a media type that doesn't parse, a URL that isn't absolute, an
/// empty file handle, and requests that are too big to process. The schema accepts the first four, so the
/// specification would have a producer skip such a part; refusing them is Beacon's policy. The SDK's
/// <c>ToChatRequestContext</c> decodes every data source eagerly and throws on bad base64 or a bad media
/// type before any event exists, and a clear 400 is better than an unhandled 500.
/// </summary>
public static class MediaInputRules
{
    public static Dictionary<string, string[]> Validate(RunAgentInput input, MediaPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);

        Dictionary<string, string[]> errors = [];
        long totalBytes = 0;

        for (var i = 0; i < (input.Messages?.Count ?? 0); i++)
        {
            if (input.Messages![i] is not AGUIUserMessage user)
            {
                continue;
            }

            var mediaParts = 0;
            for (var j = 0; j < user.Content.Count; j++)
            {
                if (user.Content[j] is AGUIMediaInputContent part)
                {
                    mediaParts++;
                    if (Check(part.Source, ref totalBytes) is { } problem)
                    {
                        errors[$"messages[{i}].content[{j}].source"] = [problem];
                    }
                }
            }

            if (mediaParts > policy.MaxPartsPerMessage)
            {
                errors[$"messages[{i}].content"] =
                    [$"A message can carry up to {policy.MaxPartsPerMessage} attachments; this one has {mediaParts}."];
            }
        }

        // The whole history counts: the client resends every earlier attachment on every turn.
        if (totalBytes > policy.MaxBytesPerRequest)
        {
            errors["messages"] =
            [
                $"The conversation carries {ByteSize.Format(totalBytes)} of attachments; Beacon accepts up to " +
                $"{ByteSize.Format(policy.MaxBytesPerRequest)} per request. Start a new conversation to send more.",
            ];
        }

        return errors;
    }

    // Each check matches something the SDK's conversion would otherwise throw on, or turn into nonsense.
    private static string? Check(AGUIInputContentSource? source, ref long totalBytes)
    {
        switch (source)
        {
            case AGUIInputContentDataSource data:
                if (!Base64.IsValid(data.Value ?? string.Empty, out var decodedLength))
                {
                    return "The data isn't valid base64.";
                }

                totalBytes += decodedLength;
                return IsMediaType(data.MimeType) ? null : "A data source needs a mimeType such as image/png.";

            case AGUIInputContentUrlSource url:
                if (!Uri.TryCreate(url.Value, UriKind.Absolute, out _))
                {
                    return "The URL isn't an absolute URL.";
                }

                return url.MimeType is null || IsMediaType(url.MimeType) ? null : "The mimeType isn't a media type.";

            case AGUIInputContentFileSource file:
                if (string.IsNullOrWhiteSpace(file.Value))
                {
                    return "A file source needs the provider's file handle.";
                }

                return file.MimeType is null || IsMediaType(file.MimeType) ? null : "The mimeType isn't a media type.";

            default:
                return "A media part needs a data, url or file source.";
        }
    }

    private static bool IsMediaType(string? value) =>
        MediaTypeHeaderValue.TryParse(value, out var parsed) && parsed.MediaType?.Contains('/', StringComparison.Ordinal) == true;
}
