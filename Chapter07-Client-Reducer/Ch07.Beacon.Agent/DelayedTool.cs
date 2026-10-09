using Microsoft.Extensions.AI;

namespace Ch07.Beacon.Agent;

/// <summary>
/// A tool that waits before it runs, standing in for a slow back-office system. Beacon's read-only
/// tools answer from memory in microseconds, too fast to see a tool call in progress on screen.
/// </summary>
internal sealed class DelayedTool(AIFunction innerFunction, TimeSpan delay) : DelegatingAIFunction(innerFunction)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        return await base.InvokeCoreAsync(arguments, cancellationToken);
    }
}
