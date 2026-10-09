using Beacon.AI;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch06.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so the test project can host this agent
// next to the Desk's WebAssembly client, which has a Program of its own.
public sealed class Program
{
    /// <summary>The CORS policy that lets Beacon Desk's browser code call the endpoint.</summary>
    public const string DeskCorsPolicy = "BeaconDesk";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUIHosting();

        var store = new InMemoryFieldServiceStore();
        builder.Services.AddSingleton<IFieldServiceStore>(store);
        IList<AITool> serverTools = new BeaconTools(store).CreateReadOnlyTools();
        HashSet<string> serverToolNames = [.. serverTools.Select(t => t.Name)];

        // Chapter 5's pipeline, plus a run log and Chapter 6's long scripted answer (the handover note).
        builder.Services.AddChatClient(sp => AIClientFactory.CreateChatClient(
                sp.GetRequiredService<IConfiguration>(), [HandoverScript.Rule, .. BeaconScripts.Default]))
            .Use((inner, services) => new RunLogChatClient(inner, services.GetRequiredService<ILogger<RunLogChatClient>>()))
            .Use((inner, services) => new ClientInstructionFilterChatClient(inner, services.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .ConfigureOptions(options =>
            {
                options.Instructions = BeaconInstructions.Default;
                options.Tools = [.. serverTools, .. (options.Tools ?? []).Where(t => !serverToolNames.Contains(t.Name))];
            })
            .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
            .UseLogging();

        // The Desk's Interactive WebAssembly page calls this endpoint from the browser, so the browser
        // has to be told that origin may: one named policy, the configured origins only, POST only.
        builder.Services.AddCors(cors => cors.AddPolicy(DeskCorsPolicy, policy => policy
            .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
            .WithMethods(HttpMethods.Post)
            .WithHeaders("Content-Type", "Accept")));

        var app = builder.Build();

        app.UseCors();

        app.MapAGUIEndpoint("/agent")
            .RequireCors(DeskCorsPolicy);

        app.Run();
    }
}
