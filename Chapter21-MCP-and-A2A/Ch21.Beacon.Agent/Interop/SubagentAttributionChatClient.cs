using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Ch21.Beacon.Agent.Interop;

/// <summary>
/// Reports a tool that delegates to another agent as a subagent on the AG-UI stream: a
/// <c>SUBAGENT_STARTED</c> tied to the tool call by <c>parentToolCallId</c> after the call is
/// announced, then <c>SUBAGENT_FINISHED</c> or <c>SUBAGENT_ERROR</c> just before its result.
/// Sits above function invocation so it sees both the call and the result. The events travel as
/// content-less updates whose <see cref="ChatResponseUpdate.RawRepresentation"/> is the event,
/// which AGUI.Server emits verbatim (Chapter 18 explains the technique).
/// </summary>
public sealed class SubagentAttributionChatClient(
    IChatClient innerClient,
    IReadOnlyDictionary<string, SubagentInfo> delegatingTools) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> active = new(StringComparer.Ordinal);

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            if (!update.Contents.Any(c => c is FunctionResultContent r && active.ContainsKey(r.CallId)))
            {
                yield return update;
            }
            else
            {
                // Function invocation returns every result of a round in one update. Split it, so
                // each invocation closes immediately before its own result.
                foreach (var content in update.Contents)
                {
                    if (content is FunctionResultContent result && active.Remove(result.CallId, out var subagentRunId))
                    {
                        yield return As(Failure(result) is { } failure
                            ? new SubagentErrorEvent { SubagentRunId = subagentRunId, Message = failure.Message, Code = failure.Code }
                            : new SubagentFinishedEvent { SubagentRunId = subagentRunId });
                    }

                    var single = update.Clone();
                    single.Contents = [content];
                    yield return single;
                }
            }

            foreach (var call in update.Contents.OfType<FunctionCallContent>())
            {
                if (delegatingTools.TryGetValue(call.Name, out var subagent))
                {
                    // One invocation per tool call, with an ID of its own: call IDs repeat across runs (a model
                    // or a restarted server can reuse "call-0001-2"), and two invocations must never share one.
                    var subagentRunId = $"{subagent.Name}-{RandomNumberGenerator.GetHexString(8, lowercase: true)}";
                    active[call.CallId] = subagentRunId;

                    yield return As(new SubagentStartedEvent
                    {
                        SubagentRunId = subagentRunId,
                        Name = subagent.Name,
                        Description = subagent.Description,
                        ParentToolCallId = call.CallId,
                        ParentMessageId = update.MessageId,
                    });
                }
            }
        }

        // Every announced invocation must close before the run finishes.
        foreach (var subagentRunId in active.Values)
        {
            yield return As(new SubagentErrorEvent { SubagentRunId = subagentRunId, Message = "The delegated call didn't complete." });
        }
    }

    private static ChatResponseUpdate As(BaseEvent evt) => new() { RawRepresentation = evt };

    // Beacon's tools report failure as {"error": "..."}; that text is already written for users. A refusal
    // of the model's own arguments says so with "code" (invalid_request); anything else is the delegate's failure.
    private static (string Message, string Code)? Failure(FunctionResultContent result) =>
        result.Exception is not null ? ("The delegated call failed.", "delegate_failed")
        : result.Result is JsonElement { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("error", out var error)
            ? (error.GetString() ?? "The delegated call failed.",
               json.TryGetProperty("code", out var code) && code.GetString() is { Length: > 0 } c ? c : "delegate_failed")
        : null;
}
