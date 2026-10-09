using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch18.Beacon.Agent.Approvals;
using Ch18.Beacon.Agent.Coordination;
using Ch18.Beacon.Agent.Specialists;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ch18.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so Ch18.Tests can host it in-process.
public sealed class Program
{
    /// <summary>The DI key of the model every part of Beacon shares; tests replace it.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // AG-UI's JSON configuration and the SSE formatter (Chapter 4).
        builder.Services.AddAGUIHosting();

        // Chapter 14's spending approvals, unchanged except that a proposal remembers which invocation made it.
        builder.Services.Configure<ApprovalOptions>(builder.Configuration.GetSection(ApprovalOptions.SectionName));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();
        builder.Services.AddSingleton<SpendingApprovalPolicy>();
        builder.Services.AddSingleton<IApprovalStore, InMemoryApprovalStore>();
        builder.Services.AddSingleton<IApprovalAudit, LoggingApprovalAudit>();
        builder.Services.AddSingleton<ResumeGate>();

        // One model for the coordinator and every specialist, as one deployment would serve them all.
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (sp, _) =>
            AIClientFactory.CreateChatClient(sp.GetRequiredService<IConfiguration>(), [.. SpecialistScripts.Rules, .. BeaconScripts.Default]));

        // The specialists, each with its own pipeline, and where a suspended one waits for its answer.
        builder.Services.Configure<SpecialistOptions>(builder.Configuration.GetSection(SpecialistOptions.SectionName));
        builder.Services.AddSingleton<SpecialistTeam>();
        builder.Services.AddSingleton<ISuspensionStore, InMemorySuspensionStore>();

        // The endpoint's pipeline: Chapter 5's instruction filter in front of the coordinator, which owns the model calls.
        builder.Services.AddChatClient(sp => sp.GetRequiredKeyedService<IChatClient>(ModelKey))
            .Use((inner, sp) => new ClientInstructionFilterChatClient(inner, sp.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .Use((inner, sp) => new BeaconCoordinator(
                inner,
                sp.GetRequiredService<SpecialistTeam>(),
                sp.GetRequiredService<ISuspensionStore>(),
                sp.GetRequiredService<IApprovalStore>(),
                sp.GetRequiredService<ILogger<BeaconCoordinator>>()))
            .UseLogging();
        builder.Services.AddSingleton<CoordinatorRunHandler>();

        var app = builder.Build();

        app.MapPost("/agent", ([FromBody] RunAgentInput input, HttpContext http, CoordinatorRunHandler handler) =>
            handler.Handle(input, http));

        app.Run();
    }
}
