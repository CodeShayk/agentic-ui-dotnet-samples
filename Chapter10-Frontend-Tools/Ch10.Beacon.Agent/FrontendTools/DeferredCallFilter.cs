using AGUI.Abstractions;

namespace Ch10.Beacon.Agent.FrontendTools;

/// <summary>
/// Removes any answer a client sends for one of Beacon's own calls that Beacon deferred, before the SDK
/// sees the request, so Beacon runs that tool itself instead of the model reading the client's version.
/// </summary>
/// <remarks>
/// <para>
/// When one model response asks for a client's tool and one of Beacon's, <c>FunctionInvokingChatClient</c>
/// runs neither, and the run ends with both unanswered (section 10.3). On the next run,
/// <c>ToChatRequestContext</c> runs every call in that response that has no answer and treats a call that
/// has one as done. A .NET client answers only its own calls. A client that follows the specification
/// derives both calls as pending and answers both, and a hostile one can answer <c>check_warranty</c> with
/// whatever it likes. Either way, without this filter the model would read the client's answer as Beacon's
/// lookup.
/// </para>
/// <para>
/// It runs as an endpoint filter because nothing in the chat pipeline runs before <c>ToChatRequestContext</c>.
/// It protects only the deferred calls of the latest response. A client can still rewrite any earlier
/// result, assistant messages included; only a history the server keeps itself prevents that (Chapter 23).
/// </para>
/// </remarks>
public sealed partial class DeferredCallFilter(IReadOnlySet<string> serverToolNames, ILogger<DeferredCallFilter> logger) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.Arguments.OfType<RunAgentInput>().FirstOrDefault() is { Messages: { Count: > 0 } messages } input
            && DeferredServerCalls(messages, serverToolNames) is { Count: > 0 } deferred)
        {
            List<AGUIMessage> kept = [];
            foreach (var message in messages)
            {
                if (message is AGUIToolMessage { ToolCallId: { } callId } && deferred.TryGetValue(callId, out var tool))
                {
                    LogDropped(logger, tool, callId);
                    continue;
                }

                kept.Add(message);
            }

            input.Messages = kept;
        }

        return next(context);
    }

    /// <summary>
    /// Beacon's calls in the latest assistant message, when that message also asked for a tool Beacon doesn't
    /// own. The server's loop runs none of a response's calls once one of them isn't its own, so nothing in
    /// the request can be Beacon's answer to them.
    /// </summary>
    public static Dictionary<string, string> DeferredServerCalls(IEnumerable<AGUIMessage> messages, IReadOnlySet<string> serverToolNames)
    {
        Dictionary<string, string> deferred = new(StringComparer.Ordinal);
        if (messages.OfType<AGUIAssistantMessage>().LastOrDefault() is not { ToolCalls: { Count: > 0 } calls }
            || calls.All(c => c.Function?.Name is { } name && serverToolNames.Contains(name)))
        {
            return deferred;
        }

        foreach (var call in calls)
        {
            if (call is { Id: { } id, Function.Name: { } name } && serverToolNames.Contains(name))
            {
                deferred.TryAdd(id, name);
            }
        }

        return deferred;
    }

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Dropped a client's answer to {ToolName} call {CallId}: Beacon deferred that call and runs it itself.")]
    private static partial void LogDropped(ILogger logger, string toolName, string callId);
}
