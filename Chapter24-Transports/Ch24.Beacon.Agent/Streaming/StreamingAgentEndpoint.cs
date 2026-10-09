using AGUI.Abstractions;
using AGUI.Formatting;
using AGUI.Server;
using Beacon.AGUI.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ch24.Beacon.Agent.Streaming;

/// <summary>
/// Beacon's AG-UI endpoint for production networks: Chapter 4's four steps, plus the decisions this
/// chapter makes about format, retries, time, bytes and silence.
/// </summary>
public static class StreamingAgentEndpoint
{
    /// <summary>The rate-limiter policy that caps how many runs stream at once.</summary>
    public const string ConcurrentRunsPolicy = "concurrent-runs";

    public static IEndpointConventionBuilder MapStreamingAgent(this IEndpointRouteBuilder endpoints, string pattern) =>
        endpoints.MapPost(pattern, (
                [FromBody] RunAgentInput input,
                HttpContext http,
                IChatClient agent,
                IEnumerable<IAGUIEventStreamFormatter> formatters,
                RunLedger ledger,
                IOptions<StreamingOptions> streamingOptions,
                IOptions<JsonOptions> jsonOptions) =>
            {
                var streaming = streamingOptions.Value;

                // 1. The wire format, chosen before anything runs: protobuf, SSE or 406.
                if (EventStreamNegotiation.Select(http.Request, formatters) is not { } formatter)
                {
                    return EventStreamNegotiation.NotAcceptable(http.Request, formatters);
                }

                if (string.IsNullOrWhiteSpace(input.ThreadId) || string.IsNullOrWhiteSpace(input.RunId))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Missing IDs",
                        detail: "A run needs a threadId and a runId.");
                }

                // 2. A run ID runs once. A retry of a request that already reached us is refused, not repeated.
                if (!ledger.TryAccept(input.ThreadId, input.RunId))
                {
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Run already received",
                        detail: $"Run {input.RunId} on thread {input.ThreadId} has already been received. Start a new run to try again.");
                }

                // 3. The run budget cancels the model, not the response, so a run that runs out of
                //    time still ends with RUN_ERROR rather than a stream that just stops.
                var budget = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
                budget.CancelAfter(streaming.RunTimeout);
                http.Response.RegisterForDispose(budget);

                var context = input.ToChatRequestContext(jsonOptions.Value.SerializerOptions);
                var updates = agent.GetStreamingResponseAsync(context.Messages, context.ChatOptions, budget.Token);
                var events = updates.AsAGUIEventStreamAsync(context, http.RequestAborted)
                    .WithProtocolVersion(AGUIProtocol.Version) // any RUN_STARTED the early start didn't write (Chapter 19)
                    .ShapeForWire(streaming.StripRawEvent, budget.Token);

                // 4. Write it unbuffered, with heartbeats when the format has a way to send one.
                return new KeepAliveEventStreamResult(events, formatter, streaming.HeartbeatInterval);
            })
            .RequireRateLimiting(ConcurrentRunsPolicy);
}
