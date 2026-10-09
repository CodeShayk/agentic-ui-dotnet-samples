using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch15.Beacon.Agent.Input;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ch15.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so Ch15.Tests can host it beside the Desk.
public sealed class Program
{
    /// <summary>The DI key of the raw model client.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // AG-UI's JSON configuration and the SSE formatter (Chapter 4).
        builder.Services.AddAGUIHosting();

        // AGUI.Server serializes every update as rawEvent, so content the pipeline invents must be serializable
        // by the endpoint's options, the way AddAGUIHosting registers the SDK's own interrupt content.
        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(1, Ch15JsonContext.Default);
            options.SerializerOptions.AddAIContentType<InputRequestedContent>(Ch15Json.InputRequestedDiscriminator);
        });

        builder.Services.Configure<InputOptions>(builder.Configuration.GetSection(InputOptions.SectionName));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();
        builder.Services.AddSingleton<IInputRequestStore, InMemoryInputRequestStore>();
        builder.Services.AddSingleton<InputForms>();
        builder.Services.AddSingleton<BackendTools>();
        builder.Services.AddSingleton<InputResumeGate>();
        builder.Services.AddSingleton<InputRunHandler>();

        // The model, chosen by configuration. The scripted one knows this chapter's WO-1003 story.
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (sp, _) =>
            AIClientFactory.CreateChatClient(sp.GetRequiredService<IConfiguration>(), [.. InputScripts.Rules, .. BeaconScripts.Default]));

        // Chapter 5's pipeline, with the input layer between FunctionInvokingChatClient and the model.
        // The first layer added is the outermost.
        builder.Services.AddChatClient(sp => sp.GetRequiredKeyedService<IChatClient>(ModelKey))
            .Use((inner, sp) => new ClientInstructionFilterChatClient(inner, sp.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .UseFunctionInvocation(configure: fic =>
            {
                fic.TerminateOnUnknownCalls = true; // a call to a client's tool ends the server's turn (Chapter 10)
                fic.MaximumIterationsPerRequest = 5;
            })
            .Use((inner, sp) => new InputRequestChatClient(
                inner,
                sp.GetRequiredService<InputForms>(),
                sp.GetRequiredService<IInputRequestStore>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<InputOptions>>(),
                sp.GetRequiredService<TimeProvider>()))
            .UseLogging();

        var app = builder.Build();

        app.MapPost("/agent", ([FromBody] RunAgentInput input, HttpContext http, InputRunHandler handler) =>
            handler.Handle(input, http));

        app.Run();
    }
}
