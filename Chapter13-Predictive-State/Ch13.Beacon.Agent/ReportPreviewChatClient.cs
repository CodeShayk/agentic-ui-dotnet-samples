using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch13.Beacon.Agent;

/// <summary>
/// Turns the arguments of <c>write_service_report</c> into predicted state while the model is still
/// writing them. <c>MapCall</c> can't: it runs once, on the complete call. This layer sees every
/// provider fragment as it passes, hands it to the run's <see cref="PredictiveStateWriter"/> and, when
/// the report a reader would see has changed, emits the writer's <c>STATE_DELTA</c> right after the
/// fragment, through <see cref="ChatResponseUpdate.RawRepresentation"/>.
/// </summary>
/// <remarks>
/// Place it above <c>UseFunctionInvocation()</c>, which passes fragments through as they arrive until
/// the model's turn produces a complete call. Without a writer on the options (a request that didn't
/// come through the AG-UI endpoint), it passes everything through untouched.
/// </remarks>
public sealed class ReportPreviewChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var writer = PredictiveStateWriter.From(options);

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update; // the fragment first: AGUI.Server turns it into TOOL_CALL_ARGS

            if (writer is null || ToolArgumentFragments.Extract(update) is not { } fragments)
            {
                continue;
            }

            foreach (var fragment in fragments)
            {
                if (writer.ReportFragment(fragment) is { } delta)
                {
                    yield return new ChatResponseUpdate { RawRepresentation = delta }; // emitted verbatim
                }
            }
        }
    }
}
