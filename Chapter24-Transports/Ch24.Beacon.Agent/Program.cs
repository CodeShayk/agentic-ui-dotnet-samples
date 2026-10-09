using Beacon.AI;
using Beacon.Domain;
using Ch24.Beacon.Agent.Pipeline;
using Ch24.Beacon.Agent.Streaming;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.AI;

namespace Ch24.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so the test project can host it
// next to the console and the proxy.
public sealed class Program
{
    /// <summary>The DI key of the pipeline behind /agent/baseline (Chapter 4's glue, unchanged).</summary>
    public const string BaselineKey = "baseline";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var streamingSection = builder.Configuration.GetSection(StreamingOptions.SectionName);
        builder.Services.Configure<StreamingOptions>(streamingSection);
        var streaming = streamingSection.Get<StreamingOptions>() ?? new StreamingOptions();

        // Kestrel's limits for long-lived responses.
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // A run input is a conversation, not an upload (Chapter 23 validates its fields).
            kestrel.Limits.MaxRequestBodySize = 256 * 1024;

            // HTTP/2: up to 100 concurrent runs per connection (the default, stated on purpose), and a
            // PING after 30 idle seconds so a dead peer is noticed and NAT mappings stay fresh.
            kestrel.Limits.Http2.MaxStreamsPerConnection = 100;
            kestrel.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(30);
            kestrel.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(20);

            // MinResponseDataRate stays at its default, 240 bytes/second after a 5-second grace. It only
            // times writes that are waiting on the client, so a quiet stream is never cut, but a client
            // that stops reading is.

            // Optional: HTTP/2 without TLS for the load test. A cleartext endpoint that also speaks
            // HTTP/1.1 refuses HTTP/2 "prior knowledge" (HTTP_1_1_REQUIRED), so it gets its own port.
            // Listening explicitly replaces the configured URLs, so the main port is listed too.
            if (streaming.Http2CleartextPort is { } h2cPort)
            {
                kestrel.ListenLocalhost(MainPort(builder.Configuration));
                kestrel.ListenLocalhost(h2cPort, listen => listen.Protocols = HttpProtocols.Http2);
            }
        });

        // AG-UI's JSON configuration and the SSE formatter (Chapter 4), plus protobuf when enabled.
        builder.Services.AddAGUIHosting();
        if (streaming.EnableProtobuf)
        {
            builder.Services.AddAGUIProtobuf();
        }

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunLedger>();

        // Backpressure at the door: beyond MaxConcurrentRuns, a new run gets 503 and Retry-After before
        // anything runs, which any client may retry after waiting that long. (Combined with Chapter 23's
        // per-user limit, set each policy's status in OnRejected: one RejectionStatusCode serves all.)
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
            limiter.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "1";
                return ValueTask.CompletedTask;
            };
            limiter.AddConcurrencyLimiter(StreamingAgentEndpoint.ConcurrentRunsPolicy, concurrency =>
            {
                concurrency.PermitLimit = streaming.MaxConcurrentRuns;
                concurrency.QueueLimit = 0;
            });
        });

        // Beacon's data and read-only tools.
        var store = new InMemoryFieldServiceStore();
        builder.Services.AddSingleton<IFieldServiceStore>(store);
        IList<AITool> serverTools = new BeaconTools(store).CreateReadOnlyTools();

        // The model, optionally slowed to a realistic time to first token for measurements.
        var firstChunkDelay = builder.Configuration.GetValue("Beacon:Simulation:FirstChunkDelay", TimeSpan.Zero);
        IChatClient CreateModel(IServiceProvider services) => new ModelLatencyChatClient(
            AIClientFactory.CreateChatClient(services.GetRequiredService<IConfiguration>()), firstChunkDelay);

        // This chapter's pipeline: the run opens before the model is called.
        builder.Services.AddChatClient(CreateModel)
            .UseEarlyRunStart()
            .UseBeaconAgent(serverTools);

        // The same agent without it, for /agent/baseline.
        builder.Services.AddKeyedChatClient(BaselineKey, CreateModel)
            .UseBeaconAgent(serverTools);

        var app = builder.Build();

        app.UseRateLimiter();

        // POST /agent: SSE or protobuf, heartbeats, no rawEvent, one run per run ID, bounded concurrency.
        app.MapStreamingAgent("/agent");

        // POST /agent/baseline: Chapter 4's glue exactly as Chapter 4 maps it (no protocolVersion stamp, rawEvent
        // kept), as the control for side-by-side measurements. Beacon's own endpoint is /agent.
        app.MapAGUIEndpoint("/agent/baseline", endpoint => endpoint.ChatClientKey = BaselineKey);

        app.Logger.LogInformation(
            "Streaming: heartbeat {Heartbeat}, rawEvent {RawEvent}, protobuf {Protobuf}, at most {MaxRuns} concurrent runs, simulated first chunk {Delay} ms",
            streaming.HeartbeatInterval, streaming.StripRawEvent ? "stripped" : "kept", streaming.EnableProtobuf ? "on" : "off",
            streaming.MaxConcurrentRuns, firstChunkDelay.TotalMilliseconds);

        app.Run();
    }

    // The port of the first configured URL (launchSettings or ASPNETCORE_URLS), 5240 if there is none.
    private static int MainPort(IConfiguration configuration) =>
        configuration["urls"]?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } url
        && Uri.TryCreate(url.Replace("*", "localhost", StringComparison.Ordinal).Replace("+", "localhost", StringComparison.Ordinal), UriKind.Absolute, out var uri)
            ? uri.Port
            : 5240;
}
