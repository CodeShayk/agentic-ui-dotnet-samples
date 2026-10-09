using System.Text.Json;
using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch10.Beacon.Agent.FrontendTools;

/// <summary>
/// Checks every result the client wrote before the model sees it. Only a result that answers a call to one
/// of the server's own tools is exempt; every other result (for a frontend tool, for a tool Beacon doesn't
/// know, or for no call at all) is replaced by its normalized form when it passes its contract and by an
/// error the model can explain when it doesn't.
/// </summary>
/// <remarks>
/// Place this layer inside <see cref="FunctionInvokingChatClient"/> (after <c>UseFunctionInvocation</c>
/// in the builder), so it sits next to the model and sees every request, including the history
/// <c>FunctionInvokingChatClient</c> builds between iterations. It changes what goes in, never what
/// comes out, and it never logs a result's content. Results the client sends for Beacon's own calls are
/// <see cref="DeferredCallFilter"/>'s job, and a server-side transcript's (Chapter 23).
/// </remarks>
public sealed partial class FrontendResultValidator(
    IChatClient innerClient,
    FrontendToolContracts contracts,
    IReadOnlySet<string> serverToolNames,
    ILogger<FrontendResultValidator> logger) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(CheckResults(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(CheckResults(messages), options, cancellationToken);

    private List<ChatMessage> CheckResults(IEnumerable<ChatMessage> messages)
    {
        var all = messages as IReadOnlyList<ChatMessage> ?? [.. messages];

        // Every call in the request first, wherever it sits, so a result placed ahead of its call is still matched.
        Dictionary<string, string> toolOfCall = new(StringComparer.Ordinal);
        foreach (var call in all.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
        {
            toolOfCall[call.CallId] = call.Name;
        }

        List<ChatMessage> checkedMessages = [];
        foreach (var message in all)
        {
            if (!message.Contents.Any(c => IsClientResult(c, toolOfCall)))
            {
                checkedMessages.Add(message);
                continue;
            }

            var copy = message.Clone();
            copy.Contents = [.. message.Contents.Select(c => IsClientResult(c, toolOfCall) && c is FunctionResultContent result
                ? Check(result, toolOfCall.GetValueOrDefault(result.CallId))
                : c)];
            checkedMessages.Add(copy);
        }

        return checkedMessages;
    }

    // Only a result for a call to one of Beacon's own tools is exempt. Anything else came from the client.
    private bool IsClientResult(AIContent content, Dictionary<string, string> toolOfCall) =>
        content is FunctionResultContent result
        && !(toolOfCall.TryGetValue(result.CallId, out var toolName) && serverToolNames.Contains(toolName));

    private FunctionResultContent Check(FunctionResultContent result, string? toolName)
    {
        FrontendToolContract? contract = null;
        var check = toolName is null
            ? FrontendResultCheck.Reject("it answers no call Beacon knows of")
            : contracts.TryGet(toolName, out contract)
                ? contract.Check(ResultText(result.Result))
                : FrontendResultCheck.Reject("Beacon has no contract for this tool, so it doesn't accept its results");

        if (check.Accepted is { } normalized)
        {
            return new FunctionResultContent(result.CallId, normalized);
        }

        LogRejected(logger, toolName ?? "(no call)", result.CallId, check.Problem!);

        // The model learns the result was unusable and why, in the server's words, never the client's.
        // Even the tool's name is repeated only when Beacon has a contract for it.
        var error = new ToolError($"Beacon couldn't use the result of {contract?.Name ?? "a tool it doesn't know"}: {check.Problem}.");
        return new FunctionResultContent(result.CallId, JsonSerializer.SerializeToElement(error, BeaconJsonContext.Default.ToolError));
    }

    // AGUI.Server hands over a tool message's content as text; results the server produced itself arrive as JSON.
    private static string ResultText(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement element => element.GetRawText(),
        _ => result.ToString() ?? string.Empty,
    };

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Rejected the result of frontend tool {ToolName} for call {CallId}: {Problem}.")]
    private static partial void LogRejected(ILogger logger, string toolName, string callId, string problem);
}
