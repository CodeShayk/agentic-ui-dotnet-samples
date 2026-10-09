using Beacon.AI;
using Beacon.Domain;
using Ch08.Beacon.Agent.Faults;
using Microsoft.Extensions.AI;

namespace Ch08.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so Ch08.Tests can host it in-process.
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUIHosting();
        builder.Services.AddHttpContextAccessor();

        // Beacon's data, its read-only tools (optionally slowed down so a call in progress is visible
        // on a phone) and one tool with a side effect, place_part_order.
        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();
        builder.Services.AddSingleton<PartOrdering>();
        builder.Services.AddSingleton(services => CreateServerTools(services, builder.Environment));

        // Chapter 5's pipeline. The first layer added is the outermost.
        builder.Services.AddChatClient(services => AIClientFactory.CreateChatClient(services.GetRequiredService<IConfiguration>()))
            .Use((inner, services) => new ClientInstructionFilterChatClient(inner, services.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .Use((inner, services) =>
            {
                var serverTools = services.GetRequiredService<IList<AITool>>();
                HashSet<string> serverToolNames = [.. serverTools.Select(t => t.Name)];
                return new ConfigureOptionsChatClient(inner, options =>
                {
                    options.Instructions = BeaconInstructions.Default;
                    options.Tools = [.. serverTools, .. (options.Tools ?? []).Where(t => !serverToolNames.Contains(t.Name))];
                });
            })
            .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
            .UseLogging();

        var app = builder.Build();

        // Development only: fail the first request of every run, so client retries can be watched.
        if (app.Environment.IsDevelopment())
        {
            app.UseFirstAttemptFault("/agent", Faults(app.Configuration).FirstAttempt);

            // What the side effect left behind, for checking that a run didn't order twice.
            app.MapGet("/dev/part-orders", (IFieldServiceStore store) => store.ListPartOrders());
        }

        app.MapAGUIEndpoint("/agent");

        app.Run();
    }

    private static IList<AITool> CreateServerTools(IServiceProvider services, IHostEnvironment environment)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var store = services.GetRequiredService<IFieldServiceStore>();
        var delay = TimeSpan.FromMilliseconds(configuration.GetValue("Beacon:ToolDelayMilliseconds", 0));

        List<AIFunction> tools =
        [
            .. new BeaconTools(store).CreateReadOnlyTools()
                .OfType<AIFunction>()
                .Select(tool => delay > TimeSpan.Zero ? new DelayedTool(tool, delay) : tool),
            services.GetRequiredService<PartOrdering>().CreateTool(),
        ];

        // Development only: drop the connection after a named tool has run.
        if (environment.IsDevelopment() && Faults(configuration).DropAfterTool is { Length: > 0 } dropAfter)
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Ch08.Beacon.Agent.Faults");
            var http = services.GetRequiredService<IHttpContextAccessor>();
            tools = [.. tools.Select(tool => tool.Name == dropAfter ? FaultInjection.DropConnectionAfter(tool, http, logger) : tool)];
        }

        return [.. tools];
    }

    private static FaultOptions Faults(IConfiguration configuration) =>
        configuration.GetSection(FaultOptions.SectionName).Get<FaultOptions>() ?? new FaultOptions();
}
