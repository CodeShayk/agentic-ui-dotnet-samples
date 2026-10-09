// Copied from Chapter 5 (Ch05.Beacon.Pipeline); each chapter's code is a self-contained checkpoint.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ch10.Beacon.Agent;

/// <summary>
/// Beacon's policy for instructions that arrive from the client: <c>system</c> and <c>developer</c>
/// messages in <c>RunAgentInput.messages</c> are dropped before the model sees them, with a warning
/// in the server log. The server's own instructions travel in <see cref="ChatOptions.Instructions"/>,
/// which no client can set.
/// </summary>
/// <remarks>
/// Place this layer first (outermost) in the pipeline so that logging, telemetry and the model all
/// see the filtered conversation. It changes what goes in, never what comes out.
/// </remarks>
public sealed partial class ClientInstructionFilterChatClient(IChatClient innerClient, ILogger? logger = null)
    : DelegatingChatClient(innerClient)
{
    /// <summary>The AG-UI <c>developer</c> role, as AGUI.Server maps it onto Microsoft.Extensions.AI.</summary>
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
                // Log the role and ID, never the content: it is untrusted and may be large.
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
