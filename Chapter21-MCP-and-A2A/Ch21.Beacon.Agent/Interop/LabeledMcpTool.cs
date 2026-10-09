using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Beacon.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ch21.Beacon.Agent.Interop;

/// <summary>
/// An allow-listed <see cref="McpClientTool"/> as Beacon offers it. The model sees Beacon's pinned
/// description and schema; the server supplies only the behavior. Every result is reduced to
/// <see cref="ExternalContentResult"/>: text only, labeled, filtered and size-limited, with the
/// MCP envelope (content blocks, <c>isError</c>, <c>_meta</c>) left behind. A failed call's message is
/// labeled and filtered like any other text from the server.
/// </summary>
public sealed partial class LabeledMcpTool(
    McpClientTool tool,
    PinnedTool pinned,
    ManualsToolSource owner,
    ManualsOptions options,
    ILogger logger) : DelegatingAIFunction(tool)
{
    // An error needs a sentence, not a manual's worth of text.
    private const int MaxErrorCharacters = 200;

    public override string Description => pinned.Description;

    public override JsonElement JsonSchema => pinned.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        CallToolResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            result = await tool.CallAsync(arguments, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Transport failures, timeouts and protocol errors all look the same to the model.
            // The details go to the log, never to the model or the AG-UI stream.
            LogCallFailed(logger, ex, Name);
            await owner.ResetAsync().ConfigureAwait(false);
            return Serialize(new ToolError("The equipment manuals service didn't answer. Try again in a minute."));
        }

        // Text blocks only. A failed call's message is the server's text too, so it is labeled and filtered the same way.
        var text = string.Join("\n\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        var labeled = result.IsError == true
            ? ExternalContent.Label(ManualsToolSource.Source, text, MaxErrorCharacters) with { Error = "The manuals service couldn't do that." }
            : ExternalContent.Label(ManualsToolSource.Source, text, options.MaxResultCharacters);
        if (labeled.RemovedPassages > 0)
        {
            LogRemoved(logger, labeled.RemovedPassages, Name);
        }

        return Serialize(labeled);
    }

    // The same options AIFunctionFactory uses for Beacon's other tools, so every result reads alike.
    private static JsonElement Serialize<T>(T value) =>
        JsonSerializer.SerializeToElement(value, (JsonTypeInfo<T>)InteropJson.Options.GetTypeInfo(typeof(T)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP tool {Tool} failed; the manuals connection will be reset.")]
    private static partial void LogCallFailed(ILogger logger, Exception exception, string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removed {Count} passage(s) addressed to AI systems from a {Tool} result.")]
    private static partial void LogRemoved(ILogger logger, int count, string tool);
}
