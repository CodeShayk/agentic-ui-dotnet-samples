using Ch19.Beacon.Adaptive.Capabilities;

namespace Ch19.Beacon.Adaptive.Planning;

/// <summary>The Beacon client features that depend on what the agent can do.</summary>
public enum BeaconFeature
{
    /// <summary>Attach a photo (an <c>image</c> input part).</summary>
    PhotoInput,

    /// <summary>Attach a recorded voice note or equipment noise (an <c>audio</c> input part).</summary>
    VoiceNote,

    /// <summary>Attach a PDF such as a service manual page (a <c>document</c> input part).</summary>
    DocumentInput,

    /// <summary>The panel that shows the agent's visible reasoning (<c>REASONING_*</c>).</summary>
    ReasoningPanel,

    /// <summary>Frontend tools that act on the client's screen (declared in <c>RunAgentInput.tools</c>).</summary>
    ScreenTools,
}

/// <summary>How a client presents one feature.</summary>
public enum Presentation
{
    /// <summary>Offer it normally.</summary>
    Offer,

    /// <summary>Offer it, with a quiet note that the agent hasn't confirmed it.</summary>
    OfferUnconfirmed,

    /// <summary>Show nothing until the event stream itself shows the feature in use.</summary>
    WhenStreamed,

    /// <summary>Offer a lesser substitute that works with any agent (a text template instead of a recording).</summary>
    Degrade,

    /// <summary>Don't offer it, and say why in plain words where the user would look for it.</summary>
    Explain,

    /// <summary>Don't offer it and say nothing: its absence costs the user nothing.</summary>
    Hide,
}

/// <summary>One feature's decision.</summary>
/// <param name="Feature">The feature.</param>
/// <param name="Declared">What the agent declared about the capability behind it.</param>
/// <param name="Presentation">How the client presents it.</param>
public sealed record FeatureDecision(BeaconFeature Feature, Support Declared, Presentation Presentation)
{
    /// <summary>The control's label, for features the client shows as a control.</summary>
    public string? Label { get; init; }

    /// <summary>The words shown beside or instead of the control, for unconfirmed, degraded and explained features.</summary>
    public string? Note { get; init; }

    /// <summary>For a degraded input: text the substitute puts in the composer for the user to complete.</summary>
    public string? Template { get; init; }

    /// <summary>True when the control is shown and captures or attaches something.</summary>
    public bool IsOffered => Presentation is Presentation.Offer or Presentation.OfferUnconfirmed;
}

/// <summary>
/// A client's whole plan for one agent: one decision per feature, plus the profile it came from.
/// Immutable; a new discovery produces a new plan.
/// </summary>
public sealed record FeaturePlan(AgentProfile Profile, IReadOnlyDictionary<BeaconFeature, FeatureDecision> Decisions)
{
    /// <summary>The attachment limit when the agent declared none for that kind.</summary>
    public const long DefaultMaxAttachmentBytes = 4 * 1024 * 1024;

    public FeatureDecision this[BeaconFeature feature] => Decisions[feature];

    public bool Has(BeaconFeature feature) => Decisions.ContainsKey(feature);

    /// <summary>Whether the client declares its screen tools on each run.</summary>
    public bool SendsScreenTools => Has(BeaconFeature.ScreenTools) && this[BeaconFeature.ScreenTools].IsOffered;

    /// <summary>The largest attachment the agent takes for an input feature: its declared limit, or a conservative default.</summary>
    public long MaxAttachmentBytes(BeaconFeature feature) =>
        Profile.AttachmentLimits.TryGetValue(LimitKey(feature), out var limit) ? limit : DefaultMaxAttachmentBytes;

    private static string LimitKey(BeaconFeature feature) => feature switch
    {
        BeaconFeature.PhotoInput => "image",
        BeaconFeature.VoiceNote => "audio",
        _ => "pdf",
    };

    /// <summary>
    /// Whether to show the reasoning panel. The event stream is authoritative: reasoning that arrives
    /// is shown whatever the declaration said.
    /// </summary>
    public bool ShowsReasoning(bool reasoningArrived) =>
        this[BeaconFeature.ReasoningPanel].Presentation switch
        {
            Presentation.Offer or Presentation.Explain => true,
            _ => reasoningArrived,
        };

    /// <summary>
    /// True when reasoning arrived from an agent that declared it doesn't reason: worth a diagnostic,
    /// never a rejected stream.
    /// </summary>
    public bool ReasoningContradictsDeclaration(bool reasoningArrived) =>
        reasoningArrived && this[BeaconFeature.ReasoningPanel].Declared == Support.Unsupported;
}
