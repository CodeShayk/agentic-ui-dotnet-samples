using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch14.Beacon.Agent.Approvals;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ch14.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so Ch14.Tests can host it in-process.
public sealed class Program
{
    /// <summary>The DI key of the raw model client; tests replace it.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // AG-UI's JSON configuration and the SSE formatter (Chapter 4).
        builder.Services.AddAGUIHosting();

        builder.Services.Configure<ApprovalOptions>(builder.Configuration.GetSection(ApprovalOptions.SectionName));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();
        builder.Services.AddSingleton<SpendingApprovalPolicy>();
        builder.Services.AddSingleton<IApprovalStore, InMemoryApprovalStore>();
        builder.Services.AddSingleton<IApprovalAudit, LoggingApprovalAudit>();
        builder.Services.AddSingleton<GatedTools>();
        builder.Services.AddSingleton<ResumeGate>();
        builder.Services.AddSingleton<ApprovalRunHandler>();

        // The model, chosen by configuration. The scripted one knows this chapter's orders and charges.
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (sp, _) =>
            AIClientFactory.CreateChatClient(sp.GetRequiredService<IConfiguration>(), [.. ApprovalScripts.Rules, .. BeaconScripts.Default]));

        // Chapter 5's pipeline, with the spending policy between FunctionInvokingChatClient and the model.
        // The first layer added is the outermost.
        builder.Services.AddChatClient(sp => sp.GetRequiredKeyedService<IChatClient>(ModelKey))
            .Use((inner, sp) => new ClientInstructionFilterChatClient(inner, sp.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .UseFunctionInvocation(configure: fic =>
            {
                fic.TerminateOnUnknownCalls = true; // a call to a client's tool ends the server's turn (Chapter 10)
                fic.MaximumIterationsPerRequest = 5;
            })
            .Use((inner, sp) => new ApprovalPolicyChatClient(
                inner, sp.GetRequiredService<SpendingApprovalPolicy>(), sp.GetRequiredService<IApprovalStore>()))
            .UseLogging();

        var app = builder.Build();

        app.MapPost("/agent", ([FromBody] RunAgentInput input, HttpContext http, ApprovalRunHandler handler) =>
            handler.Handle(input, http));

        app.Run();
    }
}
