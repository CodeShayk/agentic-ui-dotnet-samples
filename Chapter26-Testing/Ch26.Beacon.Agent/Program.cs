using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch26.Beacon.Agent.Approvals;
using Ch26.Beacon.Agent.Pipeline;
using Ch26.Beacon.Agent.Recording;
using Ch26.Beacon.Agent.State;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

namespace Ch26.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so the test projects can host it in-process.
public sealed class Program
{
    /// <summary>The DI key of the raw model client. Tests replace it with a recording.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // AG-UI's JSON configuration and the SSE formatter (Chapter 4).
        builder.Services.AddAGUIHosting();
        builder.Services.Configure<RegressionSwitches>(builder.Configuration.GetSection(RegressionSwitches.SectionName));

        // Beacon's data, its tools and the server's record of the approvals it asked for.
        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();
        builder.Services.AddSingleton<ApprovalLedger>();
        builder.Services.AddSingleton<BeaconToolset>();
        builder.Services.AddSingleton<BeaconRunHandler>();

        // The model, chosen by configuration (scripted by default).
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (services, _) =>
            AIClientFactory.CreateChatClient(services.GetRequiredService<IConfiguration>()));

        // Chapter 5's pipeline. The first layer added is the outermost; the recorder, when enabled in
        // Development, sits directly on the model.
        builder.Services.AddChatClient(services => ModelRecorder.WrapIfEnabled(services.GetRequiredKeyedService<IChatClient>(ModelKey), services))
            .UseClientInstructionFilter()
            .Use((inner, services) =>
            {
                // Server-owned instructions and tools; a client's tools keep their place unless they collide.
                var toolset = services.GetRequiredService<BeaconToolset>();
                return new ConfigureOptionsChatClient(inner, options =>
                {
                    options.Instructions = BeaconInstructions.Default;
                    options.Tools = [.. toolset.All, .. (options.Tools ?? []).Where(t => !toolset.Names.Contains(t.Name))];
                });
            })
            .UseFunctionInvocation(configure: functions =>
            {
                functions.TerminateOnUnknownCalls = true; // a call to a client's tool ends the server's turn (Chapter 10)
                functions.MaximumIterationsPerRequest = 5;
            })
            .Use((inner, services) => new SpendingGateChatClient(
                inner, services.GetRequiredService<IFieldServiceStore>(), services.GetRequiredService<ApprovalLedger>()))
            .UseLogging();

        var app = builder.Build();

        app.MapPost("/agent", ([FromBody] RunAgentInput input, HttpContext http, BeaconRunHandler handler) =>
            handler.Handle(input, http));

        app.Run();
    }
}
