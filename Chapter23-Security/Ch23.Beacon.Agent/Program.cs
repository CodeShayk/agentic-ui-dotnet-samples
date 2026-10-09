using Beacon.AI;
using Beacon.Domain;
using Ch23.Beacon.Security;
using Ch23.Beacon.Security.Approvals;
using Ch23.Beacon.Security.History;
using Ch23.Beacon.Security.Hosting;
using Ch23.Beacon.Security.Identity;
using Ch23.Beacon.Security.Logging;
using Ch23.Beacon.Security.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ch23.Beacon.Agent;

public sealed class Program
{
    /// <summary>The DI key of the raw model client; tests replace it with a recording one.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var limits = builder.Configuration.GetSection(RunInputLimits.SectionName).Get<RunInputLimits>() ?? new();

        // AG-UI's JSON configuration and the SSE formatter (Chapter 4).
        builder.Services.AddAGUIHosting();

        // Authentication: JWT bearer tokens. Issuer, audiences and signing keys are read from
        // Authentication:Schemes:Bearer (dotnet user-jwts writes them for development).
        builder.Services.AddAuthentication().AddJwtBearer(jwt =>
            jwt.IncludeErrorDetails = builder.Environment.IsDevelopment()); // don't tell strangers why a token failed
        builder.Services.AddBeaconAuthorization();

        // Beacon Desk's CORS policy and a run budget per authenticated user, shared with the Agent Framework host.
        builder.AddBeaconEndpointPolicies();

        // Classified log parameters: user IDs hashed, conversation content erased.
        builder.AddBeaconRedaction();

        builder.Services.Configure<RunInputLimits>(builder.Configuration.GetSection(RunInputLimits.SectionName));
        builder.Services.Configure<BeaconSecurityOptions>(builder.Configuration.GetSection(BeaconSecurityOptions.SectionName));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();
        builder.Services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        builder.Services.AddSingleton<RunReconciler>();
        builder.Services.AddScoped<HardenedRunHandler>();

        // Chapter 14's approvals, now keyed by user as well as thread.
        builder.Services.Configure<ApprovalOptions>(builder.Configuration.GetSection(ApprovalOptions.SectionName));
        builder.Services.AddSingleton<SpendingApprovalPolicy>();
        builder.Services.AddSingleton<IApprovalStore, InMemoryApprovalStore>();
        builder.Services.AddSingleton<IApprovalAudit, LoggingApprovalAudit>();
        builder.Services.AddSingleton<ResumeGate>();

        // The model, chosen by configuration, behind Chapter 14's pipeline: the spending policy sits between
        // FunctionInvokingChatClient and the model. Instructions and the caller's tools are set per run by
        // HardenedRunHandler, so there is no ConfigureOptions here.
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (sp, _) =>
            AIClientFactory.CreateChatClient(sp.GetRequiredService<IConfiguration>()));

        builder.Services.AddChatClient(sp => sp.GetRequiredKeyedService<IChatClient>(ModelKey))
            .UseFunctionInvocation(configure: fic =>
            {
                fic.TerminateOnUnknownCalls = true;
                fic.MaximumIterationsPerRequest = 5; // a bounded tool loop
            })
            .Use((inner, sp) => new ApprovalPolicyChatClient(
                inner, sp.GetRequiredService<SpendingApprovalPolicy>(), sp.GetRequiredService<IApprovalStore>()))
            .UseLogging()
            .UseOpenTelemetry(configure: otel => otel.EnableSensitiveData = false);

        var app = builder.Build();

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapHardenedAgent("/agent", limits.MaxRequestBodyBytes);

        // Chapter 22's thread API, behind the same policy and scoped to the caller.
        app.MapThreadEndpoints("/agent/threads");

        app.Run();
    }
}
