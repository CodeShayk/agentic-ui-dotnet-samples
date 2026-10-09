// Adapted from the AG-UI .NET SDK samples (AGUI.Samples.Shared), MIT License.
using AGUI.Abstractions;
using AGUI.Formatting;
using AGUI.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Beacon.AGUI.Hosting;

/// <summary>
/// Streams AG-UI events in the format the request's <c>Accept</c> header asks for: protobuf only when
/// it is explicitly accepted and registered, otherwise Server-Sent Events, otherwise 406 Not Acceptable.
/// </summary>
public static class AGUIResults
{
    public static IResult Events(
        IAsyncEnumerable<BaseEvent> events,
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(context);

        var formatters = context.RequestServices.GetServices<IAGUIEventStreamFormatter>().ToList();
        if (!formatters.Any(f => IsMediaType(f, SseEventStreamFormatter.ServerSentEventsMediaType)))
        {
            formatters.Add(new SseEventStreamFormatter());
        }

        var accepted = ParseAccept(context.Request);

        var protobuf = formatters.FirstOrDefault(f => IsMediaType(f, ProtobufEventStreamFormatter.ProtobufMediaType));
        if (protobuf is not null && accepted.Any(a => Allows(a) && a.MediaType.Equals(ProtobufEventStreamFormatter.ProtobufMediaType, StringComparison.OrdinalIgnoreCase)))
        {
            return new AGUIEventStreamResult(events, protobuf, cancellationToken);
        }

        var sse = formatters.First(f => IsMediaType(f, SseEventStreamFormatter.ServerSentEventsMediaType));
        var sseType = new MediaTypeHeaderValue(SseEventStreamFormatter.ServerSentEventsMediaType);
        if (accepted.Count == 0 || accepted.Any(a => Allows(a) && sseType.IsSubsetOf(a)))
        {
            return new AGUIEventStreamResult(events, sse, cancellationToken);
        }

        return Results.StatusCode(StatusCodes.Status406NotAcceptable);
    }

    private static bool IsMediaType(IAGUIEventStreamFormatter formatter, string mediaType) =>
        string.Equals(formatter.MediaType, mediaType, StringComparison.OrdinalIgnoreCase);

    private static bool Allows(MediaTypeHeaderValue entry) => entry.Quality is null or > 0;

    private static IReadOnlyList<MediaTypeHeaderValue> ParseAccept(HttpRequest request) =>
        request.Headers.Accept.Count > 0 && MediaTypeHeaderValue.TryParseList(request.Headers.Accept, out var parsed)
            ? [.. parsed]
            : [];
}

/// <summary>Writes an event stream with one formatter, unbuffered, until the stream or the request ends.</summary>
internal sealed class AGUIEventStreamResult(
    IAsyncEnumerable<BaseEvent> events,
    IAGUIEventStreamFormatter formatter,
    CancellationToken cancellationToken) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = formatter.MediaType;
        response.Headers.CacheControl = "no-cache,no-store";
        response.Headers.Pragma = "no-cache";

        // Every event must reach the client as soon as it is written.
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted, cancellationToken);

        await formatter.WriteAsync(events, response.Body, linked.Token).ConfigureAwait(false);
        await response.Body.FlushAsync(linked.Token).ConfigureAwait(false);
    }
}
