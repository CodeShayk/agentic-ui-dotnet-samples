using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch23.Beacon.Security;
using Ch23.Beacon.Security.Hosting;
using Ch23.Beacon.Security.Identity;
using Ch23.Beacon.Security.Logging;
using Ch23.Beacon.Security.Tools;
using Ch23.Beacon.Security.Validation;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ch23.Beacon.AgentFramework;

public sealed class Program
{
    /// <summary>The agent's name. MapAGUIServer looks up the session store registered under it.</summary>
    public const string AgentName = "beacon";

    /// <summary>The DI key of the raw model client; tests replace it with a recording one.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var limits = builder.Configuration.GetSection(RunInputLimits.SectionName).Get<RunInputLimits>() ?? new();
        var security = builder.Configuration.GetSection(BeaconSecurityOptions.SectionName).Get<BeaconSecurityOptions>() ?? new();

        // AG-UI serialization, with the SDK's resolver first so optional fields stay off the wire (Chapter 5).
        builder.Services.AddAGUIServer();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver));

        // Who is calling: the same JWT bearer setup and policy as Ch23.Beacon.Agent.
        builder.Services.AddAuthentication().AddJwtBearer(jwt =>
            jwt.IncludeErrorDetails = builder.Environment.IsDevelopment()); // don't tell strangers why a token failed
        builder.Services.AddBeaconAuthorization();

        // The same CORS policy and per-user run budget as Ch23.Beacon.Agent.
        builder.AddBeaconEndpointPolicies();

        // Whose sessions: the store is partitioned by the caller's NameIdentifier claim (the token's
        // "sub"), never by anything in the request. MapAGUIServer wraps the keyed store in an
        // IsolationKeyScopedAgentSessionStore that refuses to run without a key.
        builder.Services.AddHttpContextAccessor();
        builder.Services.UseClaimsBasedAgentIsolation();
        builder.Services.AddKeyedSingleton<AgentSessionStore>(AgentName, new InMemoryAgentSessionStore());

        builder.AddBeaconRedaction();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();

        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (sp, _) =>
            AIClientFactory.CreateChatClient(sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddChatClient(sp => sp.GetRequiredKeyedService<IChatClient>(ModelKey))
            .Use(inner => new RunStartedDeclarationChatClient(inner)) // outermost: declares protocolVersion (Chapter 19)
            .UseFunctionInvocation(configure: fic =>
            {
                fic.TerminateOnUnknownCalls = true;
                fic.MaximumIterationsPerRequest = 5;
            })
            .UseLogging()
            .UseOpenTelemetry(configure: otel => otel.EnableSensitiveData = false);

        var app = builder.Build();

        // The agent is a singleton, so its tools read the caller per invocation, from the same
        // HttpContext that isolation reads. The identity still comes from the token, not the model.
        var http = app.Services.GetRequiredService<IHttpContextAccessor>();
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        var time = app.Services.GetRequiredService<TimeProvider>();
        var tools = new CallerScopedTools(
            app.Services.GetRequiredService<IFieldServiceStore>(),
            () => BeaconCaller.FromPrincipal(http.HttpContext?.User ?? new())
                  ?? throw new InvalidOperationException("Tools ran without an authenticated Beacon caller."));

        AIAgent beacon = app.Services.GetRequiredService<IChatClient>()
            .AsAIAgent(
                instructions: BeaconInstructions.Default,
                name: AgentName,
                description: "Contoso Climate's field-service assistant.",
                tools: tools.CreateReadOnlyTools(),
                loggerFactory: app.Services.GetRequiredService<ILoggerFactory>(),
                services: app.Services)
            .AsBuilder()
            // The session holds the history; the client contributes user messages only, and the run has a budget.
            .Use(
                runFunc: null,
                runStreamingFunc: (messages, session, options, inner, cancellationToken) => WithinBudget(
                    budget => inner.RunStreamingAsync(UserMessagesOnly(messages, http, logger), session, options, budget),
                    security.RunTimeout, time, http, logger, cancellationToken))
            .Build(app.Services);

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapAGUIServer("/agent", beacon)
            .RequireAuthorization(BeaconPolicies.AgentUser)                      // every request, resumes included
            .RequireCors(BeaconEndpointPolicies.DeskCors)                       // browsers: Beacon Desk's origin only
            .RequireRateLimiting(BeaconEndpointPolicies.PerUserRuns)            // a budget per authenticated user
            .WithMetadata(new RequestSizeLimitAttribute(limits.MaxRequestBodyBytes)) // enforced before the JSON is parsed
            .AddEndpointFilter(new RunInputValidationFilter(limits));           // sees the bound RunAgentInput first

        app.Run();
    }

    // Past the budget the agent's token is cancelled; MapAGUIServer then ends the stream with the SDK's RUN_ERROR.
    private static async IAsyncEnumerable<AgentResponseUpdate> WithinBudget(
        Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> run,
        TimeSpan budget,
        TimeProvider time,
        IHttpContextAccessor http,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(budget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await foreach (var update in run(linked.Token).ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                SecurityLog.AgentRunTimedOut(logger, UserIdOf(http), (int)budget.TotalSeconds);
            }
        }
    }

    private static List<ChatMessage> UserMessagesOnly(IEnumerable<ChatMessage> messages, IHttpContextAccessor http, ILogger logger)
    {
        List<ChatMessage> kept = [];
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User)
            {
                kept.Add(message);
                continue;
            }

            SecurityLog.DroppedNonUserMessage(logger, message.Role.Value, message.MessageId ?? "(no id)", UserIdOf(http));
        }

        return kept;
    }

    private static string UserIdOf(IHttpContextAccessor http) =>
        http.HttpContext?.User is { } user && BeaconCaller.FromPrincipal(user) is { } caller ? caller.UserId : "(unknown)";
}
