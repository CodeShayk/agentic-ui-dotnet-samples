using Ch20.DeclarativeUi;

namespace Ch20.Beacon.Desk.Components.Surfaces;

/// <summary>What this build of Beacon Desk draws, and how it judges what arrives.</summary>
public static class DeskCatalog
{
    /// <summary>
    /// The components <see cref="SurfaceNode"/> has a case for. <c>Slider</c> is in Beacon's catalog but not
    /// here: this Desk is a release behind the agent, which is exactly the situation a consumer has to survive.
    /// </summary>
    public static IReadOnlySet<string> Renderable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Column", "Row", "Card", "Text", "Divider", "Image", "CheckBox", "TextField", "ChoicePicker", "Button",
    };

    /// <summary>A consumer's validator: the same rules as the agent's, but a component it can't draw degrades instead of failing.</summary>
    public static SurfaceValidator CreateValidator() =>
        new(SurfacePolicy.Consumer(BeaconCatalog.FieldForms, BeaconUrls.Policy, Renderable));
}

/// <summary>A button pressed on a surface.</summary>
public sealed record SurfaceActionRequest(SurfaceInstance Surface, RenderNode Button);
