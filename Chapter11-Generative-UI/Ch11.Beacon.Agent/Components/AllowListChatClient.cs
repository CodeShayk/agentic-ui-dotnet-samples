using System.Runtime.CompilerServices;
using Ch11.Beacon.Components;
using Microsoft.Extensions.AI;

namespace Ch11.Beacon.Agent.Components;

/// <summary>
/// Answers, on the server, every tool call that names a tool nobody offered, instead of letting it
/// reach a client as a call no one will run. The answer is a rejected <see cref="ComponentResult"/>
/// listing the components that do exist, and the model gets another turn to recover.
/// </summary>
/// <remarks>
/// <para>
/// Place it outside <see cref="FunctionInvokingChatClient"/> and after the layer that sets
/// <see cref="ChatOptions.Tools"/>, so it sees the full list: the server's tools, the catalog's
/// components and the client's declared tools. With <c>TerminateOnUnknownCalls</c> (Chapter 5) the
/// function-invoking layer stops at a call it can't run and hands it up; for a client's declared tool
/// that is right, and this layer leaves it alone. For a name the model made up, such as a component
/// that isn't in the catalog, this layer answers instead.
/// </para>
/// <para>
/// It only answers and retries when nothing else is waiting: if the same response also called a
/// client's tool, the run ends as usual and the refusal travels in the history of the next run.
/// </para>
/// </remarks>
public sealed partial class AllowListChatClient(
    IChatClient innerClient,
    IReadOnlyList<ComponentContract> catalog,
    ILogger<AllowListChatClient> logger,
    int maxRecoveries = 1) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        HashSet<string> offered = new(options?.Tools?.Select(t => t.Name) ?? [], StringComparer.Ordinal);
        List<ChatMessage> history = [.. messages];

        for (var attempt = 0; ; attempt++)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in base.GetStreamingResponseAsync(history, options, cancellationToken))
            {
                updates.Add(update);
                yield return update;
            }

            // The calls this response left unanswered, and which of those nobody offered.
            var response = updates.ToChatResponse();
            var contents = response.Messages.SelectMany(m => m.Contents).ToList();
            HashSet<string> answered = [.. contents.OfType<FunctionResultContent>().Select(r => r.CallId)];
            var open = contents.OfType<FunctionCallContent>().Where(c => !answered.Contains(c.CallId)).ToList();
            var unknown = open.Where(c => !offered.Contains(c.Name)).ToList();
            if (unknown.Count == 0)
            {
                yield break;
            }

            List<AIContent> refusals = [];
            foreach (var call in unknown)
            {
                LogRefused(logger, call.Name);
                refusals.Add(new FunctionResultContent(call.CallId, Refusal(call.Name).ToJsonElement()));
            }

            var answer = new ChatMessage(ChatRole.Tool, refusals) { MessageId = $"refusal-{unknown[0].CallId}" };
            yield return new ChatResponseUpdate(ChatRole.Tool, refusals) { MessageId = answer.MessageId };

            if (open.Count > unknown.Count || attempt >= maxRecoveries)
            {
                yield break; // a client's tool is pending, or the model already had its second chance
            }

            history = [.. history, .. response.Messages, answer];
        }
    }

    private ComponentResult Refusal(string toolName) =>
        ComponentResult.Rejected(
            component: null,
            errors: [$"{toolName} isn't a tool or component Beacon offers. Use one of the available components, or answer in text."],
            available: [.. catalog.Select(c => c.ToolName)]);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning, Message = "Refused a call to {ToolName}, which this request didn't offer.")]
    private static partial void LogRefused(ILogger logger, string toolName);
}
