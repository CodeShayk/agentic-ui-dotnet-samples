using Ch19.Beacon.Adaptive.Capabilities;

namespace Ch19.Beacon.Adaptive.Planning;

/// <summary>
/// Turns an <see cref="AgentProfile"/> into a <see cref="FeaturePlan"/> for Beacon Desk or Beacon Field.
/// </summary>
/// <remarks>
/// <para>
/// Supported and unsupported are easy: offer the feature, or don't. Unknown is a judgment, made per
/// feature by asking which mistake is cheaper.
/// </para>
/// <list type="bullet">
/// <item>Inputs (photo, voice note, PDF): offered, marked unconfirmed. Hiding them would treat silence as
/// "can't", which the specification forbids, and the user can still decide; the note tells them the risk.</item>
/// <item>Reasoning: shown when it streams. The stream answers the question for free, so nothing is
/// promised up front and no empty panel sits on the screen.</item>
/// <item>Screen tools: declared. A tool the agent ignores costs a few hundred bytes per run; a tool
/// withheld from an agent that would use it costs a feature.</item>
/// </list>
/// </remarks>
public static class FeaturePlanner
{
    private const string NotConfirmed = "This agent hasn't said whether it {0}. You can try; it may ignore the attachment.";

    /// <summary>Beacon Desk: photos and PDFs from the dispatcher's computer, a reasoning panel and screen tools.</summary>
    public static FeaturePlan ForDesk(AgentProfile profile) => Plan(profile,
        Photo(profile, field: false),
        Document(profile),
        Reasoning(profile),
        ScreenTools(profile));

    /// <summary>Beacon Field: photos and voice notes from the technician's phone, and reasoning when it arrives.</summary>
    public static FeaturePlan ForField(AgentProfile profile) => Plan(profile,
        Photo(profile, field: true),
        VoiceNote(profile),
        Reasoning(profile));

    private static FeaturePlan Plan(AgentProfile profile, params FeatureDecision[] decisions) =>
        new(profile, decisions.ToDictionary(d => d.Feature));

    private static FeatureDecision Photo(AgentProfile profile, bool field) => Input(
        BeaconFeature.PhotoInput, profile.ImageInput, "Photo", string.Format(NotConfirmed, "reads photos"),
        field
            // On site the technician still has the nameplate in front of them: a template keeps the flow.
            ? new FeatureDecision(BeaconFeature.PhotoInput, Support.Unsupported, Presentation.Degrade)
            {
                Label = "Type nameplate",
                Note = "This agent can't read photos, so type what the nameplate says.",
                Template = "Nameplate: model ____, serial ____",
            }
            : new FeatureDecision(BeaconFeature.PhotoInput, Support.Unsupported, Presentation.Explain)
            {
                Note = "This agent can't read photos. Type the model and serial number from the nameplate instead.",
            });

    private static FeatureDecision VoiceNote(AgentProfile profile) => Input(
        BeaconFeature.VoiceNote, profile.AudioInput, "Voice note", string.Format(NotConfirmed, "listens to recordings"),
        new FeatureDecision(BeaconFeature.VoiceNote, Support.Unsupported, Presentation.Degrade)
        {
            Label = "Describe the noise",
            Note = "This agent can't listen to recordings, so describe the sound in words.",
            Template = "The unit makes a ____ noise when ____, lasting about ____ seconds.",
        });

    private static FeatureDecision Document(AgentProfile profile) => Input(
        BeaconFeature.DocumentInput, profile.PdfInput, "PDF", string.Format(NotConfirmed, "reads PDFs"),
        new FeatureDecision(BeaconFeature.DocumentInput, Support.Unsupported, Presentation.Explain)
        {
            Note = "This agent can't read PDFs. Paste the part of the manual you need into your message.",
        });

    private static FeatureDecision Input(
        BeaconFeature feature, Support declared, string label, string unconfirmedNote, FeatureDecision whenUnsupported) =>
        declared switch
        {
            Support.Supported => new FeatureDecision(feature, declared, Presentation.Offer) { Label = label },
            Support.Unknown => new FeatureDecision(feature, declared, Presentation.OfferUnconfirmed) { Label = label, Note = unconfirmedNote },
            _ => whenUnsupported,
        };

    private static FeatureDecision Reasoning(AgentProfile profile) => profile.Reasoning switch
    {
        Support.Supported when profile.ReasoningEncrypted =>
            new FeatureDecision(BeaconFeature.ReasoningPanel, Support.Supported, Presentation.Explain)
            {
                Label = "How Beacon got there",
                Note = "This agent reasons privately; only its answers are shown.",
            },
        Support.Supported => new FeatureDecision(BeaconFeature.ReasoningPanel, Support.Supported, Presentation.Offer) { Label = "How Beacon got there" },
        Support.Unknown => new FeatureDecision(BeaconFeature.ReasoningPanel, Support.Unknown, Presentation.WhenStreamed) { Label = "How Beacon got there" },
        _ => new FeatureDecision(BeaconFeature.ReasoningPanel, Support.Unsupported, Presentation.Hide),
    };

    private static FeatureDecision ScreenTools(AgentProfile profile) => profile.ClientTools switch
    {
        Support.Unsupported => new FeatureDecision(BeaconFeature.ScreenTools, Support.Unsupported, Presentation.Explain)
        {
            Note = "This agent can't open work orders on your screen. Open them from the work-order list.",
        },
        var declared => new FeatureDecision(BeaconFeature.ScreenTools, declared, Presentation.Offer),
    };
}
