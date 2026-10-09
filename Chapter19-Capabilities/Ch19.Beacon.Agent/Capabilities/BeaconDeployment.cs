namespace Ch19.Beacon.Agent.Capabilities;

/// <summary>The two deployments this chapter's agent can play.</summary>
public enum CapabilityProfile
{
    /// <summary>Beacon on a multimodal model that reasons: photos, recordings, PDFs, visible reasoning, screen tools.</summary>
    Full,

    /// <summary>Beacon on a small text-only model: text in, text out, server tools only.</summary>
    Reduced,
}

/// <summary>
/// What this deployment does. The capabilities document is written from it and the run pipeline
/// enforces it, so the declaration and the behavior can't drift apart.
/// </summary>
public sealed record BeaconDeployment
{
    /// <summary>Beacon's own release number. It has nothing to do with the AG-UI protocol version.</summary>
    public const string AgentVersion = "2.3.0";

    public required CapabilityProfile Profile { get; init; }

    public bool AcceptsImages { get; init; }

    public bool AcceptsAudio { get; init; }

    public bool AcceptsPdf { get; init; }

    /// <summary>Whether visible reasoning (<c>REASONING_*</c>) may leave the agent.</summary>
    public bool Reasons { get; init; }

    /// <summary>Whether tools the client declares in <c>RunAgentInput.tools</c> are offered to the model.</summary>
    public bool AcceptsClientTools { get; init; }

    /// <summary>The largest attachment of each kind, decoded: Chapter 17's limits.</summary>
    public IReadOnlyDictionary<string, long> AttachmentLimits { get; init; } = new Dictionary<string, long>
    {
        ["image"] = 5 * 1024 * 1024,
        ["audio"] = 8 * 1024 * 1024,
        ["pdf"] = 10 * 1024 * 1024,
    };

    /// <summary>The most model-and-tool round trips per run (<c>FunctionInvokingChatClient.MaximumIterationsPerRequest</c>).</summary>
    public int MaxToolIterations { get; init; } = 8;

    public static BeaconDeployment For(CapabilityProfile profile) => profile switch
    {
        CapabilityProfile.Full => new()
        {
            Profile = profile,
            AcceptsImages = true,
            AcceptsAudio = true,
            AcceptsPdf = true,
            Reasons = true,
            AcceptsClientTools = true,
        },
        _ => new() { Profile = profile },
    };
}
