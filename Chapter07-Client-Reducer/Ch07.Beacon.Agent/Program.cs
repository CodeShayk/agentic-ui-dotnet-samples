using Beacon.AI;
using Beacon.Domain;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ch07.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so Ch07.Tests can host it in-process.
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUIHosting();

        // Beacon's data and its read-only tools, optionally slowed down so a tool call in progress is
        // visible in the Desk (Beacon:ToolDelayMilliseconds).
        var store = new InMemoryFieldServiceStore();
        builder.Services.AddSingleton<IFieldServiceStore>(store);
        var toolDelay = TimeSpan.FromMilliseconds(builder.Configuration.GetValue("Beacon:ToolDelayMilliseconds", 0));
        IList<AITool> serverTools = [.. new BeaconTools(store).CreateReadOnlyTools()
            .Select(tool => toolDelay > TimeSpan.Zero && tool is AIFunction function ? new DelayedTool(function, toolDelay) : tool)];
        HashSet<string> serverToolNames = [.. serverTools.Select(t => t.Name)];
        var restateConversation = builder.Configuration.GetValue("Beacon:RestateConversation", true);

        // Chapter 5's pipeline. The first layer added is the outermost.
        builder.Services.AddChatClient(sp => AIClientFactory.CreateChatClient(sp.GetRequiredService<IConfiguration>()))
            .Use((inner, services) => new ClientInstructionFilterChatClient(inner, services.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .Use((inner, services) => restateConversation
                ? new MessagesSnapshotChatClient(inner, services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions)
                : inner)
            .ConfigureOptions(options =>
            {
                options.Instructions = BeaconInstructions.Default;
                options.Tools = [.. serverTools, .. (options.Tools ?? []).Where(t => !serverToolNames.Contains(t.Name))];
            })
            .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
            .UseLogging();

        var app = builder.Build();

        app.MapAGUIEndpoint("/agent");

        app.Run();
    }
}
