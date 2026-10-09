using System.Security.Cryptography;
using System.Text.Json;
using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch09.Beacon.Agent.Tools;

/// <summary>
/// Turns a tool's unexpected failure into a result that the model can explain and the Desk can show,
/// and keeps the details for the people who fix it. Expected failures, such as a work order that
/// doesn't exist, are already results: the tool returns a <see cref="ToolError"/>. This boundary
/// handles everything else, in the same <c>{"error": "..."}</c> shape.
/// </summary>
/// <param name="tool">The tool to guard.</param>
/// <param name="systemName">How the user would name the system behind the tool, such as "the warranty service".</param>
/// <param name="logger">Where the failure's details go.</param>
public sealed partial class ToolErrorBoundary(AIFunction tool, string systemName, ILogger logger) : DelegatingAIFunction(tool)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // One short reference ties what the dispatcher sees to the log entry that explains it.
            var reference = RandomNumberGenerator.GetHexString(6);
            var callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId ?? "(unknown)";
            LogToolFailed(logger, ex, Name, callId, reference);

            // What the model sees, and through TOOL_CALL_RESULT the Desk: no exception text, no host names.
            var error = new ToolError(
                $"{char.ToUpperInvariant(systemName[0])}{systemName[1..]} did not answer, so this lookup failed. " +
                $"This is usually temporary. (Reference {reference})");

            return JsonSerializer.SerializeToElement(error, BeaconJsonContext.Default.ToolError);
        }
    }

    // The arguments and the exception's message stay out of the message template: arguments can carry
    // customer data, and the exception (with its stack) travels as the log entry's exception.
    [LoggerMessage(EventId = 9001, Level = LogLevel.Error, Message = "Tool {ToolName} failed on call {ToolCallId} (reference {Reference}).")]
    private static partial void LogToolFailed(ILogger logger, Exception exception, string toolName, string toolCallId, string reference);
}
