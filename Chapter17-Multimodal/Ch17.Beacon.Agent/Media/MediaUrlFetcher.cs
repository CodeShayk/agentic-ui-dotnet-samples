using System.Net;
using Ch17.Media;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ch17.Beacon.Agent.Media;

/// <summary>The outcome of fetching a URL source: the bytes as <see cref="DataContent"/>, or why not.</summary>
public sealed record MediaFetchResult(DataContent? Content, MediaRefusalCode? Code, string? Reason)
{
    public static MediaFetchResult Refused(string reason) => new(null, MediaRefusalCode.UrlRefused, reason);
}

/// <summary>
/// Fetches URL sources for the model, so they get the same checks as inline data. Redirects are followed
/// by hand, at most <see cref="MediaOptions.MaxRedirects"/> of them, and every hop is checked as if the
/// client had sent it: a redirect is just another URL the client chose.
/// </summary>
public sealed class MediaUrlFetcher(
    IHttpClientFactory httpClientFactory, UrlSourceGuard guard, MediaPolicy policy, IOptions<MediaOptions> options)
{
    /// <summary>The named <see cref="HttpClient"/> whose handler connects through <see cref="UrlSourceGuard.ConnectAsync"/>.</summary>
    public const string HttpClientName = "beacon-media";

    /// <summary>The handler behind <see cref="HttpClientName"/>: no redirects, cookies, credentials, proxy or decompression.</summary>
    public static SocketsHttpHandler CreateHandler(UrlSourceGuard guard) => new()
    {
        AllowAutoRedirect = false,            // each hop is checked by FetchAsync
        UseCookies = false,
        UseProxy = false,                     // a proxy would resolve the name itself, past the address check
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = guard.ConnectAsync, // the address check, on the address actually connected to
    };

    public async Task<MediaFetchResult> FetchAsync(Uri url, string? declaredMediaType, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.FetchTimeoutSeconds));
        var http = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            var current = url;
            for (var hop = 0; ; hop++)
            {
                if (guard.CheckUrl(current) is { } urlProblem)
                {
                    return MediaFetchResult.Refused(hop == 0 ? urlProblem : $"it redirected to a URL Beacon won't fetch: {urlProblem}");
                }

                if (await guard.CheckAddressesAsync(current.IdnHost, timeout.Token).ConfigureAwait(false) is { } addressProblem)
                {
                    return MediaFetchResult.Refused(hop == 0 ? addressProblem : $"it redirected to a host Beacon won't fetch from: {addressProblem}");
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= options.Value.MaxRedirects)
                    {
                        return MediaFetchResult.Refused("it redirected too many times");
                    }

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return MediaFetchResult.Refused($"the host answered with status {(int)response.StatusCode}");
                }

                if (response.Content.Headers.ContentLength > policy.LargestFile)
                {
                    return MediaFetchResult.Refused($"the file is over {ByteSize.Format(policy.LargestFile)}");
                }

                var bytes = await ReadAtMostAsync(response.Content, policy.LargestFile, timeout.Token).ConfigureAwait(false);
                if (bytes is null)
                {
                    return MediaFetchResult.Refused($"the file is over {ByteSize.Format(policy.LargestFile)}");
                }

                // A URL source's mimeType is optional; without one, the bytes decide. With one, they must agree.
                var verdict = MediaInspection.Inspect(IsDeclared(declaredMediaType) ? declaredMediaType : MediaSniffer.Detect(bytes), bytes, policy);
                return verdict.Accepted
                    ? new MediaFetchResult(new DataContent(bytes, verdict.Rule!.MediaType), null, null)
                    : new MediaFetchResult(null, verdict.Code, verdict.Reason);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return MediaFetchResult.Refused($"the host didn't answer within {options.Value.FetchTimeoutSeconds} seconds");
        }
        catch (HttpRequestException ex) when (ex.InnerException is MediaFetchRefusedException refused)
        {
            return MediaFetchResult.Refused(refused.Message);
        }
        catch (HttpRequestException)
        {
            return MediaFetchResult.Refused("the file couldn't be downloaded");
        }
    }

    // UriContent always has a media type: the SDK infers one from the extension, or uses "image/*",
    // "audio/*", "video/*" or "application/octet-stream" when it can't. Those mean "not declared".
    private static bool IsDeclared(string? mediaType) =>
        mediaType is { Length: > 0 } && !mediaType.EndsWith("/*", StringComparison.Ordinal)
        && !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]?> ReadAtMostAsync(HttpContent content, long limit, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                return null; // stop reading: a server that lies about Content-Length gets no further
            }
        }

        return buffer.ToArray();
    }
}
