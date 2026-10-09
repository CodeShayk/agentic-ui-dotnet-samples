using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Server;
using Ch19.Beacon.Adaptive.Versioning;
using Microsoft.Extensions.AI;

namespace Ch19.Beacon.Agent.Versioning;

/// <summary>
/// The downgrade Beacon offers a client that declares no protocol version. Such a client predates the field,
/// and with it subagent support: <c>SUBAGENT_STARTED</c>, <c>SUBAGENT_FINISHED</c> and <c>SUBAGENT_ERROR</c>
/// are event types it fails on while decoding. This layer drops those three for it and keeps
/// <c>subagentRunId</c> on everything else, a field an older parser ignores. Dropping the lifecycle loses what
/// a reader would have seen, so each run that loses it logs a warning naming it. It changes no identifier,
/// outcome or order. Put it above anything that emits subagent events, such as Chapter 18's coordinator.
/// </summary>
public sealed partial class SubagentDowngradeChatClient(IChatClient innerClient, ILogger<SubagentDowngradeChatClient> logger)
    : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RunAgentInput? input = null;
        var undeclared = options is not null && options.TryGetRunAgentInput(out input)
            && ProtocolVersions.Judge(input.ProtocolVersion) == VersionVerdict.Absent;
        var dropped = 0;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            if (undeclared && update.RawRepresentation is SubagentStartedEvent or SubagentFinishedEvent or SubagentErrorEvent)
            {
                dropped++;
                continue;
            }

            yield return update;
        }

        if (dropped > 0)
        {
            LogDropped(logger, input!.RunId, dropped);
        }
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    [LoggerMessage(EventId = 1905, Level = LogLevel.Warning,
        Message = "Run {RunId}: the client declared no AG-UI protocol version, so {Count} subagent lifecycle events (SUBAGENT_STARTED, SUBAGENT_FINISHED, SUBAGENT_ERROR) were left out and only subagentRunId attribution was sent. A client that declares AG-UI 1.0 receives them.")]
    private static partial void LogDropped(ILogger logger, string runId, int count);
}
