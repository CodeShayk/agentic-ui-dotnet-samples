using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ch21.Beacon.Agent;

/// <summary>
/// Chapter 5's instruction policy, copied into this checkpoint: client-sent <c>system</c> and
/// <c>developer</c> messages are dropped before the model sees them, with a warning in the log.
/// </summary>
public sealed partial class ClientInstructionFilterChatClient(IChatClient innerClient, ILogger? logger = null)
    : DelegatingChatClient(innerClient)
{
    public static readonly ChatRole DeveloperRole = new("developer");

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(WithoutClientInstructions(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(WithoutClientInstructions(messages), options, cancellationToken);

    private List<ChatMessage> WithoutClientInstructions(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> kept = [];
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System || message.Role == DeveloperRole)
            {
                LogDropped(_logger, message.Role.Value, message.MessageId ?? "(no id)");
                continue;
            }

            kept.Add(message);
        }

        return kept;
    }

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Warning,
        Message = "Dropped a client-sent {Role} message ({MessageId}). Beacon's instructions come from the server.")]
    private static partial void LogDropped(ILogger logger, string role, string messageId);
}

public static class ClientInstructionFilterExtensions
{
    public static ChatClientBuilder UseClientInstructionFilter(this ChatClientBuilder builder) =>
        builder.Use((innerClient, services) => new ClientInstructionFilterChatClient(
            innerClient,
            services.GetService<ILoggerFactory>()?.CreateLogger<ClientInstructionFilterChatClient>()));
}
