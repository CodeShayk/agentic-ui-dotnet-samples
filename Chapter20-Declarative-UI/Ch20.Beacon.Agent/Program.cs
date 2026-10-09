using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch20.Beacon.Agent.Inspections;
using Ch20.Beacon.Agent.Surfaces;
using Ch20.DeclarativeUi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ch20.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so Ch20.Tests can host it beside the Desk.
public sealed class Program
{
    /// <summary>The DI key of the raw model client. Tests replace it.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // AG-UI's JSON configuration and the SSE formatter (Chapter 4).
        builder.Services.AddAGUIHosting();

        builder.Services.Configure<SurfaceOptions>(builder.Configuration.GetSection(SurfaceOptions.SectionName));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();

        // What the model may describe: Beacon's field-forms catalog, checked as a producer checks, strictly.
        builder.Services.AddSingleton(new SurfaceValidator(SurfacePolicy.Producer(BeaconCatalog.FieldForms, BeaconUrls.Policy)));
        builder.Services.AddSingleton<InspectionTemplates>();
        builder.Services.AddSingleton<InspectionRules>();
        builder.Services.AddSingleton<InspectionRecorder>();
        builder.Services.AddSingleton<SurfaceLedger>();
        builder.Services.AddSingleton<RenderSurfaceTool>();
        builder.Services.AddSingleton<BeaconToolbox>();
        builder.Services.AddSingleton<SurfaceActionGate>();
        builder.Services.AddSingleton<SurfaceRunHandler>();

        // The model: the scripted one by default, with this chapter's rules in front of Beacon's defaults.
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (services, _) =>
            AIClientFactory.CreateChatClient(services.GetRequiredService<IConfiguration>(), [.. InspectionScript.Rules, .. BeaconScripts.Default]));

        // Chapter 5's pipeline. The first layer added is the outermost.
        builder.Services.AddChatClient(services => services.GetRequiredKeyedService<IChatClient>(ModelKey))
            .Use((inner, services) => new ClientInstructionFilterChatClient(inner, services.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .UseFunctionInvocation(configure: fic =>
            {
                fic.TerminateOnUnknownCalls = true; // a call to a client's tool ends the server's turn (Chapter 10)
                fic.MaximumIterationsPerRequest = 6;
            })
            .UseLogging();

        var app = builder.Build();

        app.MapPost("/agent", ([FromBody] RunAgentInput input, HttpContext http, SurfaceRunHandler handler) => handler.Handle(input, http));

        app.Run();
    }
}
