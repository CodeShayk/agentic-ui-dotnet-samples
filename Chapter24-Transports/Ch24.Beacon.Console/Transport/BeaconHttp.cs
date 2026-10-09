using System.Net;
using AGUI.Client;
using AGUI.Formatting;
using AGUI.Protobuf;

namespace Ch24.Beacon.Console.Transport;

/// <summary>Which wire formats a client offers in its <c>Accept</c> header, in order of preference.</summary>
public enum WireFormat
{
    /// <summary><c>text/event-stream</c> only: Beacon Desk and any client without the protobuf package.</summary>
    Sse,

    /// <summary>Protobuf first, SSE as the fallback: Beacon Field.</summary>
    Protobuf,

    /// <summary>Protobuf and nothing else. A mistake, kept here to show the 406 it causes.</summary>
    ProtobufOnly,
}

/// <summary>Builds the <see cref="HttpClient"/> a Beacon client streams runs through.</summary>
public static class BeaconHttp
{
    /// <summary>The handlers under a Beacon client's <see cref="HttpClient"/>: negotiation, Retry-After, the version and the sockets.</summary>
    public static HttpMessageHandler CreateHandler(WireFormat wire, bool http2 = false, bool multipleHttp2Connections = false)
    {
        // AGUIEventStreamHandler writes these into Accept, in order, and decodes whichever format the
        // response's Content-Type names. SSE stays on the list, because a server may not speak protobuf.
        IAGUIEventStreamFormatter[] formatters = wire switch
        {
            WireFormat.Protobuf => [new ProtobufEventStreamFormatter(), new SseEventStreamFormatter()],
            WireFormat.ProtobufOnly => [new ProtobufEventStreamFormatter()],
            _ => [new SseEventStreamFormatter()],
        };

        var sockets = new SocketsHttpHandler
        {
            // HTTP/2 allows 100 concurrent streams per connection by default. Without this, run 101
            // waits for one of the first hundred to finish.
            EnableMultipleHttp2Connections = multipleHttp2Connections,

            // Pick up DNS changes (a redeployed agent) instead of holding one connection forever.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),

            // An unreachable agent still fails fast. A connect timeout means the run never started.
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        // HTTP/2 over cleartext needs "prior knowledge": ask for 2.0 exactly. (Over TLS, 2.0 with
        // RequestVersionOrLower lets ALPN settle on what both ends speak.) Either way the version goes on
        // each request: HttpClient.DefaultRequestVersion doesn't reach requests the AG-UI transport builds.
        HttpMessageHandler inner = http2
            ? new RequestVersionHandler(HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact) { InnerHandler = sockets }
            : sockets;

        // The SDK's transport drops a refusal's headers, so Retry-After is read here (Chapter 8).
        inner = new RetryAfterHandler { InnerHandler = inner };

        return new AGUIEventStreamHandler(formatters) { InnerHandler = inner };
    }

    public static HttpClient Create(WireFormat wire, bool http2 = false, bool multipleHttp2Connections = false) =>
        new(CreateHandler(wire, http2, multipleHttp2Connections))
        {
            // Off, as in Chapter 6: it would bound only the wait for headers. Each run has its own bound.
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
