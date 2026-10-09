using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch16.Beacon.Agent.Reasoning;
using Ch16.Beacon.Agent.Scripted;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ch16.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so Ch16.Tests can host it beside the Desk.
public sealed class Program
{
    /// <summary>The DI key of the raw model client.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // AG-UI's JSON configuration and the SSE formatter (Chapter 4).
        builder.Services.AddAGUIHosting();

        // What reasoning may be shown, and so stored, by any client: the server's call, from configuration.
        builder.Services.Configure<ReasoningPolicy>(builder.Configuration.GetSection(ReasoningPolicy.SectionName));

        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();
        builder.Services.AddSingleton<BeaconTools>();
        builder.Services.AddSingleton<ReasoningRunHandler>();

        // The model, chosen by configuration. The scripted one plays a provider that seals its reasoning.
        builder.Services.AddSingleton<ScriptedReasoningSeal>();
        builder.Services.AddSingleton<DiagnosisScripts>();
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (sp, _) => AIClientFactory.CreateChatClient(
            sp.GetRequiredService<IConfiguration>(),
            [.. sp.GetRequiredService<DiagnosisScripts>().Rules, .. BeaconScripts.Default]));

        // Chapter 5's pipeline with the reasoning policy outside FunctionInvokingChatClient, where it sees the
        // whole run as the converter will. The first layer added is the outermost.
        builder.Services.AddChatClient(sp => sp.GetRequiredKeyedService<IChatClient>(ModelKey))
            .Use((inner, sp) => new ClientInstructionFilterChatClient(inner, sp.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .Use((inner, sp) => new ReasoningPolicyChatClient(
                inner,
                sp.GetRequiredService<IOptions<ReasoningPolicy>>(),
                sp.GetService<ILogger<ReasoningPolicyChatClient>>()))
            .UseFunctionInvocation(configure: fic =>
            {
                fic.TerminateOnUnknownCalls = true; // a call to a client's tool ends the server's turn (Chapter 10)
                fic.MaximumIterationsPerRequest = 5;
            })
            .UseLogging();

        var app = builder.Build();

        app.MapPost("/agent", ([FromBody] RunAgentInput input, HttpContext http, ReasoningRunHandler handler) =>
            handler.Handle(input, http));

        app.Run();
    }
}
