using System.Net.Http.Headers;
using System.Net.Sockets;
using AGUI.Formatting;

namespace Ch08.Field.Core.Connection;

/// <summary>
/// The <see cref="HttpClient"/> Beacon Field talks to its agent with: <see cref="SocketsHttpHandler"/> on
/// every platform, a connect timeout that reads as a connection failure, pooled connections that don't
/// outlive a network change, the server's <c>Retry-After</c> kept for the retry layer and no timeout on
/// the wait for the first event.
/// </summary>
public static class FieldHttp
{
    /// <summary>Creates the client. The caller owns it and should keep one for the app's lifetime.</summary>
    /// <param name="connectTimeout">How long to wait for a TCP connection before calling the attempt failed.</param>
    public static HttpClient CreateClient(TimeSpan connectTimeout)
    {
        var http = new HttpClient(new RetryAfterHandler { InnerHandler = CreateHandler(connectTimeout) })
        {
            // Beacon's server writes nothing, not even headers, until the model's first update, so a header
            // timeout would fail a slow model that is working fine. The turn has its own bound
            // (ConversationOptions.TurnTimeout), and the connect has the one below.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // The SSE binding asks clients to say what they accept; the SDK's transport doesn't.
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(SseEventStreamFormatter.ServerSentEventsMediaType));
        return http;
    }

    /// <summary>Creates the handler: a bounded connect that fails as a connection error, and short-lived pooled connections.</summary>
    /// <param name="connectTimeout">How long to wait for a TCP connection.</param>
    public static SocketsHttpHandler CreateHandler(TimeSpan connectTimeout) => new()
    {
        // When a phone moves from Wi-Fi to cellular, pooled connections point at a network that's gone.
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),

        // SocketsHttpHandler.ConnectTimeout expires as a TaskCanceledException, the type HttpClient.Timeout
        // also throws for a request the server may be running (Chapter 6). Connecting here instead makes a
        // slow connect a SocketException, which the handler reports as HttpRequestError.ConnectionError:
        // the request never left, so it's safe to retry.
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(connectTimeout);
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, connect.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw new SocketException((int)SocketError.TimedOut);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
