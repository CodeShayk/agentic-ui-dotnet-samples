using AGUI.Formatting;
using AGUI.Protobuf;
using Microsoft.Net.Http.Headers;

namespace Ch24.Beacon.Agent.Streaming;

/// <summary>
/// Chooses the wire format for a run from the request's <c>Accept</c> header, with Chapter 4's rules:
/// protobuf only when the client names its media type and the server registered the formatter;
/// Server-Sent Events for <c>text/event-stream</c>, <c>*/*</c> or no <c>Accept</c> at all; otherwise
/// nothing, which the endpoint answers with 406 before the model is called.
/// </summary>
/// <remarks>
/// Both rules depart from the spec's protobuf binding, on purpose. A wildcard admits protobuf there, and a
/// producer that supports it SHOULD then use it; Beacon answers <c>*/*</c> with SSE, because curl and most
/// generic clients send it and can't read binary frames. And the binding says a producer MUST answer SSE
/// whenever <c>Accept</c> doesn't admit protobuf; Beacon answers 406 instead (<c>application/json</c> and
/// <c>application/*</c> included), so a client that can't read SSE finds out before the run, not in a parser.
/// </remarks>
public static class EventStreamNegotiation
{
    private static readonly MediaTypeHeaderValue SseType = new(SseEventStreamFormatter.ServerSentEventsMediaType);

    public static IAGUIEventStreamFormatter? Select(HttpRequest request, IEnumerable<IAGUIEventStreamFormatter> formatters)
    {
        var registered = formatters.ToList();
        IList<MediaTypeHeaderValue> accepted =
            request.Headers.Accept.Count > 0 && MediaTypeHeaderValue.TryParseList(request.Headers.Accept, out var parsed)
                ? parsed
                : [];

        // Protobuf is opt-in: a wildcard admits it by the letter of the spec, but curl and most HTTP
        // clients send */* by default and can't read it, so only an explicit request selects it.
        if (registered.FirstOrDefault(f => Is(f, ProtobufEventStreamFormatter.ProtobufMediaType)) is { } protobuf
            && accepted.Any(a => Allows(a) && a.MediaType.Equals(ProtobufEventStreamFormatter.ProtobufMediaType, StringComparison.OrdinalIgnoreCase)))
        {
            return protobuf;
        }

        if (accepted.Count == 0 || accepted.Any(a => Allows(a) && SseType.IsSubsetOf(a)))
        {
            return registered.FirstOrDefault(f => Is(f, SseEventStreamFormatter.ServerSentEventsMediaType)) ?? new SseEventStreamFormatter();
        }

        return null;
    }

    /// <summary>
    /// A 406 that says what the endpoint can produce, so the client's developer doesn't have to guess.
    /// </summary>
    public static IResult NotAcceptable(HttpRequest request, IEnumerable<IAGUIEventStreamFormatter> formatters)
    {
        var offered = formatters.Select(f => f.MediaType).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!offered.Contains(SseEventStreamFormatter.ServerSentEventsMediaType, StringComparer.OrdinalIgnoreCase))
        {
            offered.Add(SseEventStreamFormatter.ServerSentEventsMediaType);
        }

        return Results.Problem(
            statusCode: StatusCodes.Status406NotAcceptable,
            title: "No acceptable event stream format",
            detail: $"Accept was '{request.Headers.Accept}'. This endpoint streams {string.Join(" or ", offered)}.");
    }

    private static bool Is(IAGUIEventStreamFormatter formatter, string mediaType) =>
        string.Equals(formatter.MediaType, mediaType, StringComparison.OrdinalIgnoreCase);

    private static bool Allows(MediaTypeHeaderValue entry) => entry.Quality is null or > 0;
}
