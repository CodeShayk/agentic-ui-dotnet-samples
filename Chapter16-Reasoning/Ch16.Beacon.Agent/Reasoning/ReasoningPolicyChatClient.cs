using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ch16.Beacon.Agent.Reasoning;

/// <summary>
/// Applies Beacon's <see cref="ReasoningPolicy"/> to every run. On the way in it asks the provider for no more
/// reasoning than the policy will show (<see cref="ChatOptions.Reasoning"/>). On the way out it shapes the
/// reasoning before <c>AGUI.Server</c> turns it into <c>REASONING_*</c> events: redacted, capped or withheld, with
/// every sealed value attached to a reasoning message the client already knows.
/// </summary>
/// <remarks>
/// Place it outside <c>FunctionInvokingChatClient</c>: it then sees the run's whole stream, as the converter
/// does, while the model still gets its own unshaped reasoning back between tool calls.
/// </remarks>
public sealed partial class ReasoningPolicyChatClient(
    IChatClient innerClient,
    IOptions<ReasoningPolicy> policy,
    ILogger<ReasoningPolicyChatClient>? logger = null) : DelegatingChatClient(innerClient)
{
    private readonly ReasoningPolicy _policy = policy.Value;

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var shaper = new ReasoningShaper(_policy);

        await foreach (var update in base.GetStreamingResponseAsync(messages, Request(options), cancellationToken).ConfigureAwait(false))
        {
            foreach (var shaped in shaper.Shape(update))
            {
                yield return shaped;
            }
        }

        foreach (var shaped in shaper.Complete())
        {
            yield return shaped;
        }

        // Counts only. Reasoning text never goes to the log.
        if (logger is not null && (shaper.WithheldCharacters > 0 || shaper.Redacted.Count > 0))
        {
            LogShaped(logger, _policy.Disclosure, shaper.WithheldCharacters, shaper.Redacted.Count > 0 ? string.Join(", ", shaper.Redacted) : "nothing");
        }
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    // Don't ask for what won't be shown. Providers treat this as a hint (OllamaSharp ignores Output), so the
    // shaper enforces the policy on whatever comes back anyway.
    private ChatOptions Request(ChatOptions? options)
    {
        var request = options?.Clone() ?? new ChatOptions();
        request.Reasoning = new ReasoningOptions
        {
            Effort = request.Reasoning?.Effort,
            Output = _policy.Disclosure switch
            {
                ReasoningDisclosure.Full => ReasoningOutput.Full,
                ReasoningDisclosure.Summary => ReasoningOutput.Summary,
                _ => ReasoningOutput.None,
            },
        };

        return request;
    }

    [LoggerMessage(EventId = 1601, Level = LogLevel.Information,
        Message = "Reasoning policy {Disclosure}: withheld {Characters} characters; redacted {Redactions}.")]
    private static partial void LogShaped(ILogger logger, ReasoningDisclosure disclosure, int characters, string redactions);
}
