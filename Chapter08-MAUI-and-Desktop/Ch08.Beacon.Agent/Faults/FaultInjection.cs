using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ch08.Beacon.Agent.Faults;

/// <summary>Development-only fault injection for the AG-UI endpoint. Never enable it in production.</summary>
public static partial class FaultInjection
{
    /// <summary>
    /// Fails the first request for every run ID before the run starts, by closing the connection or
    /// answering 503. A client that retries with the same input gets through on its second attempt;
    /// Beacon Field retries only the 503, because a closed connection doesn't show that the run never started.
    /// </summary>
    public static IApplicationBuilder UseFirstAttemptFault(this IApplicationBuilder app, string agentPath, FirstAttemptFault fault)
    {
        if (fault == FirstAttemptFault.None)
        {
            return app;
        }

        ConcurrentDictionary<string, byte> seen = new(StringComparer.Ordinal);
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Ch08.Beacon.Agent.Faults");

        return app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsPost(context.Request.Method) || context.Request.Path != agentPath)
            {
                await next(context);
                return;
            }

            // Peek at the run ID, then rewind so the endpoint reads the body as usual.
            context.Request.EnableBuffering();
            string? runId = null;
            try
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                runId = body.RootElement.TryGetProperty("runId", out var value) ? value.GetString() : null;
            }
            catch (JsonException)
            {
                // Let the endpoint reject it.
            }

            context.Request.Body.Position = 0;

            if (runId is not null && seen.TryAdd(runId, 0))
            {
                LogFirstAttemptFailed(logger, runId, fault);
                if (fault == FirstAttemptFault.Abort)
                {
                    context.Abort();
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = "1";
                return;
            }

            await next(context);
        });
    }

    /// <summary>Wraps <paramref name="tool"/> so the connection drops right after it runs.</summary>
    public static AIFunction DropConnectionAfter(AIFunction tool, IHttpContextAccessor http, ILogger logger) =>
        new DropConnectionAfterTool(tool, http, logger);

    private sealed class DropConnectionAfterTool(AIFunction inner, IHttpContextAccessor http, ILogger logger)
        : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // The side effect happens; the result never reaches the client.
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            LogDropped(logger, Name);

            // A reset discards bytes the client hasn't read yet, so without a pause the call's own frames
            // can vanish too. Waiting a moment makes the fault repeatable: everything up to TOOL_CALL_END
            // arrives, and nothing after it.
            await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
            http.HttpContext?.Abort();
            return result;
        }
    }

    [LoggerMessage(EventId = 8101, Level = LogLevel.Warning,
        Message = "Fault: failed the first attempt of run {RunId} before it started ({Fault}).")]
    private static partial void LogFirstAttemptFailed(ILogger logger, string runId, FirstAttemptFault fault);

    [LoggerMessage(EventId = 8102, Level = LogLevel.Warning,
        Message = "Fault: dropped the connection after {Tool} ran.")]
    private static partial void LogDropped(ILogger logger, string tool);
}
