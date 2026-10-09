namespace Ch23.Beacon.Security;

/// <summary>Budgets for runs and callers, bound from <c>Beacon:Security</c>. Approvals keep Chapter 14's <c>Beacon:Approvals</c>.</summary>
public sealed class BeaconSecurityOptions
{
    public const string SectionName = "Beacon:Security";

    /// <summary>The longest a whole run may take, tool loops included. The model call is cancelled after it.</summary>
    public int RunTimeoutSeconds { get; set; } = 120;

    /// <summary>Runs one authenticated user may start per minute.</summary>
    public int RunsPerMinute { get; set; } = 30;

    public TimeSpan RunTimeout => TimeSpan.FromSeconds(RunTimeoutSeconds);
}
