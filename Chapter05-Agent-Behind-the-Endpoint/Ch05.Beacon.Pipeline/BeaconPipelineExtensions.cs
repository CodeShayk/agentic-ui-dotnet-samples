using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ch05.Beacon.Pipeline;

/// <summary><see cref="ChatClientBuilder"/> extensions for Beacon's pipeline.</summary>
public static class BeaconPipelineExtensions
{
    /// <summary>
    /// Adds <see cref="ClientInstructionFilterChatClient"/>, which drops client-sent <c>system</c> and
    /// <c>developer</c> messages. Call it first so every later layer sees the filtered conversation.
    /// </summary>
    public static ChatClientBuilder UseClientInstructionFilter(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Use((innerClient, services) => new ClientInstructionFilterChatClient(
            innerClient,
            services.GetService<ILoggerFactory>()?.CreateLogger<ClientInstructionFilterChatClient>()));
    }
}
