namespace Ch19.Beacon.Adaptive.Planning;

/// <summary>What pressing an input button does.</summary>
public enum InputAction
{
    /// <summary>Nothing: the button isn't shown.</summary>
    None,

    /// <summary>Capture or pick media and put it in the attachment tray.</summary>
    Capture,

    /// <summary>Put a text template in the composer for the user to complete (the degraded substitute).</summary>
    InsertTemplate,
}

/// <summary>
/// One composer button, derived from a <see cref="FeatureDecision"/> so every client presents the plan
/// the same way. The caption is visible text, also used as the control's accessible description.
/// </summary>
public sealed record InputButton(BeaconFeature Feature, bool IsVisible, string Text, string? Caption, InputAction Action, string? Template)
{
    public static InputButton For(FeatureDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.Presentation switch
        {
            Presentation.Offer => new(decision.Feature, true, decision.Label ?? decision.Feature.ToString(), null, InputAction.Capture, null),
            Presentation.OfferUnconfirmed => new(decision.Feature, true, decision.Label ?? decision.Feature.ToString(), decision.Note, InputAction.Capture, null),
            Presentation.Degrade => new(decision.Feature, true, decision.Label ?? decision.Feature.ToString(), decision.Note, InputAction.InsertTemplate, decision.Template),
            Presentation.Explain => new(decision.Feature, false, string.Empty, decision.Note, InputAction.None, null),
            _ => new(decision.Feature, false, string.Empty, null, InputAction.None, null),
        };
    }
}
