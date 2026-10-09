using Ch19.Beacon.Adaptive.Capabilities;

namespace Ch19.Beacon.Adaptive.Planning;

/// <summary>The words both clients use when they show the plan to a person.</summary>
public static class FeatureText
{
    public static string Name(BeaconFeature feature) => feature switch
    {
        BeaconFeature.PhotoInput => "Photos",
        BeaconFeature.VoiceNote => "Voice notes",
        BeaconFeature.DocumentInput => "PDFs",
        BeaconFeature.ReasoningPanel => "Reasoning",
        BeaconFeature.ScreenTools => "Screen tools",
        _ => feature.ToString(),
    };

    public static string Declared(Support support) => support switch
    {
        Support.Supported => "yes",
        Support.Unsupported => "no",
        _ => "not declared",
    };

    public static string Presented(Presentation presentation) => presentation switch
    {
        Presentation.Offer => "offered",
        Presentation.OfferUnconfirmed => "offered, marked unconfirmed",
        Presentation.WhenStreamed => "shown if the agent sends it",
        Presentation.Degrade => "replaced by a text template",
        Presentation.Explain => "not offered; explained",
        _ => "hidden",
    };
}
