using AGUI.Abstractions;
using AGUI.Formatting;
using Microsoft.AspNetCore.Http.Features;

namespace Ch24.Beacon.Agent.Streaming;

/// <summary>
/// Writes an AG-UI event stream unbuffered, one event per formatter call, and fills any silence longer
/// than the heartbeat interval with an SSE comment line. The SSE binding requires consumers to
/// tolerate comments; the protobuf binding has no frame that isn't an event, so protobuf streams get
/// no heartbeats.
/// </summary>
public sealed class KeepAliveEventStreamResult(
    IAsyncEnumerable<BaseEvent> events,
    IAGUIEventStreamFormatter formatter,
    TimeSpan heartbeatInterval,
    TimeProvider? timeProvider = null) : IResult
{
    private static readonly byte[] KeepAlive = ": keep-alive\n\n"u8.ToArray();

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = formatter.MediaType;
        response.Headers.CacheControl = "no-cache,no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no"; // nginx, and proxies that copy it: don't buffer this response
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var time = timeProvider ?? TimeProvider.System;
        var heartbeats = heartbeatInterval > TimeSpan.Zero
            && string.Equals(formatter.MediaType, SseEventStreamFormatter.ServerSentEventsMediaType, StringComparison.OrdinalIgnoreCase);

        // Cancelled when the client leaves or this method exits for any reason, so the run never outlives its stream.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
        var enumerator = events.GetAsyncEnumerator(stop.Token);
        Task<bool>? next = null;
        try
        {
            while (true)
            {
                next ??= enumerator.MoveNextAsync().AsTask();
                if (heartbeats && !next.IsCompleted)
                {
                    try
                    {
                        await next.WaitAsync(heartbeatInterval, time, stop.Token);
                    }
                    catch (TimeoutException)
                    {
                        // Nothing to say for a while: a comment line shows every hop the stream is alive.
                        await response.Body.WriteAsync(KeepAlive, stop.Token);
                        await response.Body.FlushAsync(stop.Token);
                        continue;
                    }
                }

                if (!await next)
                {
                    break;
                }

                next = null;

                // One event per call, so a heartbeat can only ever fall between two complete frames.
                await formatter.WriteAsync(One(enumerator.Current), response.Body, stop.Token);
                await response.Body.FlushAsync(stop.Token);
            }
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            // The client left. Nobody is reading, so no terminal event is owed; the run is cancelled below.
        }
        finally
        {
            await stop.CancelAsync();
            if (next is { IsCompleted: false })
            {
                // Let the pending MoveNextAsync observe the cancellation before the enumerator is disposed.
                await ((Task)next).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await enumerator.DisposeAsync();
        }
    }

    private static IAsyncEnumerable<BaseEvent> One(BaseEvent evt) => new[] { evt }.ToAsyncEnumerable();
}
