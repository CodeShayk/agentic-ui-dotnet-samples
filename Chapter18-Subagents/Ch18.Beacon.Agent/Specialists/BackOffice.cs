using Microsoft.Extensions.AI;

namespace Ch18.Beacon.Agent.Specialists;

/// <summary>How the specialists behave: how long they may take, and how slow (or broken) the systems behind them are.</summary>
public sealed class SpecialistOptions
{
    public const string SectionName = "Beacon:Specialists";

    /// <summary>How long one specialist invocation may take before the coordinator reports it failed.</summary>
    public double TimeoutSeconds { get; set; } = 20;

    /// <summary>How long each back-office lookup takes. Beacon's data is in memory; real systems aren't.</summary>
    public int ToolDelayMilliseconds { get; set; }

    /// <summary>Equipment whose warranty provider is down: lookups for it never answer.</summary>
    public string[] WarrantyOutage { get; set; } = [];

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
}

/// <summary>
/// A back-office lookup that takes a while, standing in for a real system (Chapter 13's <c>DelayedTool</c>).
/// Without it the specialists would finish before anyone could see them working side by side.
/// </summary>
internal sealed class BackOfficeLatency(AIFunction innerFunction, TimeSpan delay) : DelegatingAIFunction(innerFunction)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        return await base.InvokeCoreAsync(arguments, cancellationToken);
    }
}

/// <summary>
/// The warranty provider's outage: a lookup for affected equipment never answers, so the warranty
/// specialist runs out of time. The call is cancelled with the invocation, as a real HTTP call would be.
/// </summary>
internal sealed class WarrantyOutage(AIFunction innerFunction, IReadOnlySet<string> affected) : DelegatingAIFunction(innerFunction)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (arguments.TryGetValue("equipmentId", out var equipmentId) && equipmentId?.ToString() is { } id && affected.Contains(id))
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
        }

        return await base.InvokeCoreAsync(arguments, cancellationToken);
    }
}
