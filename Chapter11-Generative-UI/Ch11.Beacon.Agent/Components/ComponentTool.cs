using System.Text.Json;
using Ch11.Beacon.Components;
using Microsoft.Extensions.AI;

namespace Ch11.Beacon.Agent.Components;

/// <summary>
/// One catalog component as a server tool. The model sees the component's prop schema as the tool's
/// parameters, so "calling" the component means writing its props. Invoking the tool renders nothing:
/// it decides whether the props may be rendered and answers with a <see cref="ComponentResult"/>.
/// </summary>
/// <param name="contract">The component's contract: tool name, description and prop schema.</param>
/// <param name="rules">The server's checks against its own data, run once the props match the schema.</param>
/// <param name="logger">Where rejections are logged (the tool name and how many problems, never the props).</param>
public sealed partial class ComponentTool(
    ComponentContract contract,
    Func<JsonElement, ComponentResult> rules,
    ILogger logger) : AIFunction
{
    public ComponentContract Contract => contract;

    public override string Name => contract.ToolName;

    public override string Description => contract.Description;

    public override JsonElement JsonSchema => contract.Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // The arguments as the model wrote them: one JSON object, the props.
        var props = JsonSerializer.SerializeToElement(arguments, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, object?>)));

        // First the shape, then the facts. A rejection goes back to the model in the same run, so it can
        // fix the props or answer in text, and to the client, which shows a fallback instead.
        var errors = contract.Validate(props);
        var result = errors.Count > 0 ? ComponentResult.Rejected(contract.ComponentName, errors) : rules(props);

        if (!result.IsAccepted)
        {
            LogRejected(logger, contract.ToolName, result.Errors?.Count ?? 0);
        }

        return ValueTask.FromResult<object?>(result.ToJsonElement());
    }

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning, Message = "Rejected a {ToolName} call with {ProblemCount} problem(s).")]
    private static partial void LogRejected(ILogger logger, string toolName, int problemCount);
}
