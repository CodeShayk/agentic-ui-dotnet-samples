// Copied from Chapter 7; each chapter's code is a self-contained checkpoint.
using Microsoft.Extensions.AI;

namespace Ch13.Beacon.Agent;

/// <summary>
/// A tool that waits before it runs, standing in for a slow back-office system. Beacon's tools answer
/// from memory in microseconds, too fast to see a plan step or a check in progress on screen.
/// </summary>
internal sealed class DelayedTool(AIFunction innerFunction, TimeSpan delay) : DelegatingAIFunction(innerFunction)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        return await base.InvokeCoreAsync(arguments, cancellationToken);
    }
}
