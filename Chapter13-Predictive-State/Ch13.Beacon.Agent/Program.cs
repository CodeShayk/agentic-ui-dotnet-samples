using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AGUI.Hosting;
using Beacon.AI;
using Beacon.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ch13.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so Ch13.Tests can host it beside the Desk.
public sealed class Program
{
    /// <summary>The DI key of the model client at the bottom of the pipeline. Tests replace it.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var configuration = builder.Configuration;

        builder.Services.AddAGUIHosting();

        // Beacon's data, the reports it saves and its tools. Lookups take a moment, like the real back office.
        var store = new InMemoryFieldServiceStore();
        var archive = new ServiceReportArchive();
        builder.Services.AddSingleton<IFieldServiceStore>(store);
        builder.Services.AddSingleton(archive);
        var lookupDelay = TimeSpan.FromMilliseconds(configuration.GetValue("Beacon:ToolDelayMilliseconds", 0));
        var saveDelay = TimeSpan.FromMilliseconds(configuration.GetValue("Beacon:ReportSaveDelayMilliseconds", 0));
        IList<AITool> serverTools =
        [
            .. new BeaconTools(store).CreateReadOnlyTools()
                .Select(tool => lookupDelay > TimeSpan.Zero && tool is AIFunction function ? new DelayedTool(function, lookupDelay) : tool),
            .. new DiagnosticTools(store, archive).Create(saveDelay),
        ];
        HashSet<string> serverToolNames = [.. serverTools.Select(t => t.Name)];

        // The model: scripted by default, with this chapter's rules in front of Beacon's defaults.
        var ai = AIClientFactory.ReadOptions(configuration);
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (_, _) =>
            AIClientFactory.CreateChatClient(ai, [.. DiagnosticScript.Rules, .. BeaconScripts.Default]));

        // The scripted model sends each call whole; this makes it stream arguments the way OpenAI does (Chapter 9).
        var streamArguments = ai.Provider == AIProvider.Scripted && configuration.GetValue("Beacon:StreamToolArguments", true);
        var fragmentDelay = TimeSpan.FromMilliseconds(configuration.GetValue("Beacon:ArgumentFragmentDelayMilliseconds", 30));
        var fragmentLength = configuration.GetValue("Beacon:ArgumentFragmentLength", 12);

        // Chapter 5's pipeline. The first layer added is the outermost.
        builder.Services.AddChatClient(services => services.GetRequiredKeyedService<IChatClient>(ModelKey))
            .Use((inner, services) => new ClientInstructionFilterChatClient(inner, services.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .Use(inner => new DiagnosticProgressChatClient(inner))   // steps and the activity
            .Use(inner => new ReportPreviewChatClient(inner))        // the report, while its arguments stream
            .ConfigureOptions(options =>
            {
                options.Instructions = BeaconInstructions.Default + DiagnosticScript.Instructions;
                options.Tools = [.. serverTools, .. (options.Tools ?? []).Where(t => !serverToolNames.Contains(t.Name))];
            })
            .UseFunctionInvocation(configure: functions =>
            {
                functions.TerminateOnUnknownCalls = true;   // a client's tool ends the run (Chapter 10)
                functions.AllowConcurrentInvocation = true; // calls from one model turn run side by side
            })
            .UseLogging()
            .Use(inner => streamArguments ? new ScriptedArgumentStreaming(inner, fragmentDelay, fragmentLength) : inner);

        var app = builder.Build();

        // Chapter 4's four steps, written out because each run needs one collaborator on both sides of
        // step 3: the PredictiveStateWriter that the stream options' mappers and the chat pipeline share.
        app.MapPost("/agent", (
            [FromBody] RunAgentInput input,
            IOptions<JsonOptions> jsonOptions,
            IChatClient agent,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var state = new PredictiveStateWriter(input.State);
            var context = input.ToChatRequestContext(jsonOptions.Value.SerializerOptions, BeaconStreamOptions.Create(state));
            state.AttachTo(context.ChatOptions);

            var updates = agent.GetStreamingResponseAsync(context.Messages, context.ChatOptions, cancellationToken);
            return AGUIResults.Events(updates.AsAGUIEventStreamAsync(context, cancellationToken), httpContext, cancellationToken);
        });

        app.Run();
    }
}
