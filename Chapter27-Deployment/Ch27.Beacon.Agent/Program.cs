using Beacon.AI;
using Beacon.Domain;
using Ch27.Beacon.Agent.Conversations;
using Ch27.Beacon.Agent.Hosting;
using Ch27.Beacon.Agent.Pipeline;
using Ch27.Beacon.Agent.Runs;
using Ch27.Beacon.Agent.Security;
using Ch27.Beacon.Agent.Security.Approvals;
using Ch27.Beacon.Agent.Security.History;
using Ch27.Beacon.Agent.Security.Hosting;
using Ch27.Beacon.Agent.Security.Identity;
using Ch27.Beacon.Agent.Security.Logging;
using Ch27.Beacon.Agent.Security.Validation;
using Ch27.Beacon.Agent.Streaming;
using Ch27.Beacon.Agent.Telemetry;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;

namespace Ch27.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so the test project can host the agent
// next to the Desk and the smoke test.
public sealed class Program
{
    /// <summary>The DI key of the raw model client; tests replace it.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var limits = builder.Configuration.GetSection(RunInputLimits.SectionName).Get<RunInputLimits>() ?? new();
        var streaming = builder.Configuration.GetSection(StreamingOptions.SectionName).Get<StreamingOptions>() ?? new();
        var runLimits = builder.Configuration.GetSection(RunLimitsOptions.SectionName).Get<RunLimitsOptions>() ?? new();

        // Telemetry, in an order that matters. Chapter 23's redaction replaces the logger factory; Chapter 25's
        // content gate wraps whatever factory is registered and puts its span redactor ahead of the exporters
        // that AddServiceDefaults adds.
        builder.AddBeaconRedaction();
        var sensitiveData = builder.AddBeaconContentGate();
        builder.AddServiceDefaults();
        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(RunCostMetrics.MeterName));

        // AG-UI's JSON configuration and the SSE formatter (Chapter 4), plus protobuf (Chapter 24).
        builder.Services.AddAGUIHosting();
        if (streaming.EnableProtobuf)
        {
            builder.Services.AddAGUIProtobuf();
        }

        // Chapter 23: a bearer token on every request. Authority, audiences and keys come from
        // Authentication:Schemes:Bearer, which the deployment keeps in its secret store.
        builder.Services.AddAuthentication().AddJwtBearer(jwt => jwt.IncludeErrorDetails = builder.Environment.IsDevelopment());
        builder.Services.AddBeaconAuthorization();
        builder.AddBeaconEndpointPolicies(); // CORS, 429 per user (Chapter 23), 503 per replica (Chapter 24)

        builder.Services.Configure<RunInputLimits>(builder.Configuration.GetSection(RunInputLimits.SectionName));
        builder.Services.Configure<BeaconSecurityOptions>(builder.Configuration.GetSection(BeaconSecurityOptions.SectionName));
        builder.Services.Configure<ApprovalOptions>(builder.Configuration.GetSection(ApprovalOptions.SectionName));
        builder.Services.Configure<StreamingOptions>(builder.Configuration.GetSection(StreamingOptions.SectionName));
        builder.Services.Configure<ConversationStoreOptions>(builder.Configuration.GetSection(ConversationStoreOptions.SectionName));
        builder.Services.Configure<RunLimitsOptions>(builder.Configuration.GetSection(RunLimitsOptions.SectionName));
        builder.Services.Configure<PricingOptions>(builder.Configuration.GetSection(PricingOptions.SectionName));

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();

        // Chapter 22's durable store, holding Chapter 23's per-user transcript.
        builder.Services.AddSingleton<FileConversationStore>();
        builder.Services.AddSingleton<IConversationStore>(sp => sp.GetRequiredService<FileConversationStore>());
        builder.Services.AddHostedService<ConversationRetentionService>();

        // Chapter 23's reconciler and Chapter 14's approvals, keyed by user as well as thread; Chapter 24's ledger.
        builder.Services.AddSingleton<RunReconciler>();
        builder.Services.AddSingleton<SpendingApprovalPolicy>();
        builder.Services.AddSingleton<IApprovalStore, InMemoryApprovalStore>();
        builder.Services.AddSingleton<IApprovalAudit, LoggingApprovalAudit>();
        builder.Services.AddSingleton<ResumeGate>();
        builder.Services.AddSingleton<RunLedger>();
        builder.Services.AddSingleton<RunCostMetrics>();
        builder.Services.AddScoped<HardenedRunHandler>();

        // This chapter: a budget for every run and a drain on shutdown, with readiness checks that report them.
        builder.Services.AddSingleton<RunGovernor>();
        builder.Services.AddHealthChecks()
            .AddCheck<DrainHealthCheck>("drain")
            .AddCheck<ModelConfigurationHealthCheck>("model-configuration");
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(runLimits.DrainSeconds + 5));
        builder.Services.AddRequestTimeouts(timeouts =>
            timeouts.DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(10) });

        // The model, chosen by configuration, behind the combined pipeline (outermost first): the early RUN_STARTED
        // (Chapter 24), the run budget and drain (this chapter), the run's cost (Chapter 25), the bounded tool loop
        // and Chapter 14's spending policy (Chapter 23), then logging and spans with the content decision.
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (sp, _) =>
            AIClientFactory.CreateChatClient(sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddChatClient(sp => sp.GetRequiredKeyedService<IChatClient>(ModelKey))
            .Use(inner => new EarlyRunStartChatClient(inner))
            .Use((inner, sp) => new GovernedChatClient(inner, sp.GetRequiredService<RunGovernor>()))
            .UseRunCost()
            .UseFunctionInvocation(configure: fic =>
            {
                fic.TerminateOnUnknownCalls = true;
                fic.MaximumIterationsPerRequest = 5;
            })
            .Use((inner, sp) => new ApprovalPolicyChatClient(
                inner, sp.GetRequiredService<SpendingApprovalPolicy>(), sp.GetRequiredService<IApprovalStore>()))
            .UseLogging()
            .UseOpenTelemetry(configure: otel => otel.EnableSensitiveData = sensitiveData);

        // Behind the ingress, ASPNETCORE_FORWARDEDHEADERS_ENABLED=true (set by Aspire's Container Apps output)
        // applies X-Forwarded-For and -Proto. Don't also call UseForwardedHeaders(): a second pass would believe
        // an X-Forwarded-For entry the caller wrote.

        var app = builder.Build();

        app.UseRequestTimeouts();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        // POST /agent and the thread API, behind the same policy; GET /health and /alive for the probes.
        app.MapHardenedAgent("/agent", limits.MaxRequestBodyBytes, app.Services.GetRequiredService<RunGovernor>());
        app.MapThreadEndpoints("/agent/threads");
        app.MapDefaultEndpoints();

        app.Run();
    }
}
