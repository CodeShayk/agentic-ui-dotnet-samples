using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch24.Beacon.Agent.Pipeline;

/// <summary><see cref="ChatClientBuilder"/> extensions for Beacon's pipeline.</summary>
public static class BeaconPipelineExtensions
{
    /// <summary>Adds <see cref="EarlyRunStartChatClient"/>. Call it first, so it is the outermost layer.</summary>
    public static ChatClientBuilder UseEarlyRunStart(this ChatClientBuilder builder) =>
        builder.Use(inner => new EarlyRunStartChatClient(inner));

    /// <summary>
    /// Chapter 5's agent layers: client instructions dropped, Beacon's instructions and tools added,
    /// server tools run here and client tools handed back to the client.
    /// </summary>
    public static ChatClientBuilder UseBeaconAgent(this ChatClientBuilder builder, IList<AITool> serverTools)
    {
        HashSet<string> serverToolNames = [.. serverTools.Select(t => t.Name)];

        return builder
            .Use((inner, services) => new ClientInstructionFilterChatClient(
                inner, services.GetService<ILoggerFactory>()?.CreateLogger<ClientInstructionFilterChatClient>()))
            .ConfigureOptions(options =>
            {
                options.Instructions = BeaconInstructions.Default;
                options.Tools = [.. serverTools, .. (options.Tools ?? []).Where(t => !serverToolNames.Contains(t.Name))];
            })
            .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true);
    }
}
