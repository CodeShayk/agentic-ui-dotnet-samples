using Ch11.Beacon.ComponentViews;

namespace Ch11.Beacon.Field;

/// <summary>
/// Beacon Field's component registry: a native template per component name, chosen per row. It plays the
/// part <c>DynamicComponent</c> plays on the Desk, and falls back the same way: a component that isn't
/// known, was refused or has nothing to draw gets the fallback template.
/// </summary>
public sealed class ComponentTemplateSelector : DataTemplateSelector
{
    public DataTemplate Message { get; set; } = default!;

    public DataTemplate PartCard { get; set; } = default!;

    public DataTemplate ScheduleTimeline { get; set; } = default!;

    public DataTemplate FaultHistoryChart { get; set; } = default!;

    public DataTemplate Fallback { get; set; } = default!;

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) => item switch
    {
        TextItem => Message,
        ComponentItem { Component: { IsKnown: true, View: not null, Phase: not (ComponentPhase.Rejected or ComponentPhase.Abandoned) } component } =>
            component.ComponentName switch
            {
                "PartCard" => PartCard,
                "ScheduleTimeline" => ScheduleTimeline,
                "FaultHistoryChart" => FaultHistoryChart,
                _ => Fallback,
            },
        _ => Fallback,
    };
}
