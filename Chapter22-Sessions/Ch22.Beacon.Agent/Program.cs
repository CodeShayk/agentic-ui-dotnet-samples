using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch22.Beacon.Agent.Compaction;
using Ch22.Beacon.Agent.Conversations;
using Ch22.Beacon.Agent.Readings;
using Ch22.Beacon.Agent.Runs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ch22.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so Ch22.Tests can host it beside the Desk.
public sealed class Program
{
    /// <summary>The DI key of the raw model client. Compaction calls it directly, with no tools.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // AG-UI's JSON configuration and the SSE formatter (Chapter 4).
        builder.Services.AddAGUIHosting();

        // Where conversations live, how long they stay and how much of them the model sees.
        builder.Services.Configure<ConversationStoreOptions>(builder.Configuration.GetSection(ConversationStoreOptions.SectionName));
        builder.Services.Configure<CompactionOptions>(builder.Configuration.GetSection(CompactionOptions.SectionName));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IConversationStore, FileConversationStore>();
        builder.Services.AddHostedService<ConversationRetentionService>();

        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();
        builder.Services.AddSingleton<BeaconTools>();
        builder.Services.AddSingleton<LineVoltageReadings>();
        builder.Services.AddSingleton<RunPlanner>();
        builder.Services.AddSingleton(sp => new HistoryCompactor(
            sp.GetRequiredKeyedService<IChatClient>(ModelKey),
            sp.GetRequiredService<IOptions<CompactionOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<HistoryCompactor>>()));
        builder.Services.AddSingleton<ConversationRunHandler>();

        // The model, chosen by configuration. The scripted one knows this chapter's WO-1005 story.
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (sp, _) =>
            AIClientFactory.CreateChatClient(sp.GetRequiredService<IConfiguration>(), SessionScripts.Create()));

        // Chapter 5's pipeline, with two layers under function invocation: input requests become interrupts,
        // and every message and call gets an ID that stays unique for the life of the conversation.
        builder.Services.AddChatClient(sp => sp.GetRequiredKeyedService<IChatClient>(ModelKey))
            .Use((inner, sp) => new ClientInstructionFilterChatClient(inner, sp.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .UseFunctionInvocation(configure: fic =>
            {
                fic.TerminateOnUnknownCalls = true; // a call to a client's tool ends the server's turn (Chapter 10)
                fic.MaximumIterationsPerRequest = 5;
            })
            .Use((inner, sp) => new ReadingRequestChatClient(inner, sp.GetRequiredService<LineVoltageReadings>()))
            .Use(inner => new UniqueIdsChatClient(inner))
            .UseLogging();

        var app = builder.Build();

        app.MapPost("/agent", ([FromBody] RunAgentInput input, HttpContext http, ConversationRunHandler handler) =>
            handler.HandleAsync(input, http));

        app.MapThreadEndpoints("/agent/threads");

        app.Run();
    }
}
