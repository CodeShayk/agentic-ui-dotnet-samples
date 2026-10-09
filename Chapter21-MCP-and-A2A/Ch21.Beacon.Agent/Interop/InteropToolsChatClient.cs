using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch21.Beacon.Agent.Interop;

/// <summary>
/// Adds the tools that reach other systems to each run: the supplier's agent always, and the
/// allow-listed manual tools whenever the MCP server is reachable. Discovery is asynchronous,
/// which is why this is a delegating client rather than part of <c>ConfigureOptions</c>.
/// </summary>
public sealed class InteropToolsChatClient(
    IChatClient innerClient,
    ManualsToolSource manuals,
    AIFunction supplierTool) : DelegatingChatClient(innerClient)
{
    // Reserved whether or not the manuals server is up, so a client can never declare a lookalike.
    private readonly HashSet<string> _reserved = [.. ManualsToolSource.AllowedTools.Keys, supplierTool.Name];

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await base.GetResponseAsync(messages, await WithInteropToolsAsync(options, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var withTools = await WithInteropToolsAsync(options, cancellationToken).ConfigureAwait(false);
        await foreach (var update in base.GetStreamingResponseAsync(messages, withTools, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private async ValueTask<ChatOptions> WithInteropToolsAsync(ChatOptions? options, CancellationToken cancellationToken)
    {
        var manualTools = await manuals.GetToolsAsync(cancellationToken).ConfigureAwait(false);

        var result = options?.Clone() ?? new ChatOptions();
        result.Tools = [.. (options?.Tools ?? []).Where(t => !_reserved.Contains(t.Name)), supplierTool, .. manualTools];
        return result;
    }
}
