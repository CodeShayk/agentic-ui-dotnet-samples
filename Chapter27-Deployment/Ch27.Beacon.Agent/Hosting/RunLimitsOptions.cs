namespace Ch27.Beacon.Agent.Hosting;

/// <summary>How long a run may take, and how long runs in flight get when the replica shuts down.</summary>
public sealed class RunLimitsOptions
{
    public const string SectionName = "Beacon:Hosting";

    /// <summary>
    /// The longest a run may take, model calls and tools included. A policy choice, not a platform fact: Azure
    /// Container Apps documents a 240-second request timeout for HTTP ingress without saying whether it counts the
    /// whole request or idle time, and 210 seconds stays short of either reading, so a run that runs long ends
    /// with <c>RUN_ERROR</c> instead of a connection cut by the proxy.
    /// </summary>
    public double RunBudgetSeconds { get; set; } = 210;

    /// <summary>
    /// After shutdown begins, how long runs in flight may continue before they are stopped with
    /// <c>RUN_ERROR</c>. Keep it below the host's shutdown timeout and the platform's termination
    /// grace period (30 seconds by default on Azure Container Apps).
    /// </summary>
    public double DrainSeconds { get; set; } = 20;
}
