using System.Text.Json;
using Ch11.Beacon.Components;

namespace Ch11.Beacon.ComponentViews;

/// <summary>
/// What a client knows about one component: its contract (tool name, prop schema, typed props) and how
/// to turn props into the view model its platform templates render.
/// </summary>
public interface IComponentKind
{
    ComponentContract Contract { get; }

    /// <summary>A short name for sentences, such as "part card".</summary>
    string DisplayName { get; }

    /// <summary>The view model type, which is the type of the component's <c>View</c> parameter.</summary>
    Type ViewType { get; }

    /// <summary>Builds the view model; <see langword="null"/> when the props can't be read as this component's props.</summary>
    IComponentView? TryCreateView(JsonElement props);
}

/// <summary>A component kind with typed props and a typed view model.</summary>
public sealed class ComponentKind<TProps, TView>(ComponentContract contract, string displayName, Func<TProps, TView> createView) : IComponentKind
    where TProps : class
    where TView : class, IComponentView
{
    public ComponentContract Contract => contract;

    public string DisplayName => displayName;

    public Type ViewType => typeof(TView);

    public IComponentView? TryCreateView(JsonElement props)
    {
        try
        {
            // Drafts are read with the same typed props as final components; a value of the wrong type
            // (a price written as text, say) makes the props unreadable rather than wrong.
            return props.Deserialize(contract.PropsType) is TProps typed ? createView(typed) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Beacon's three components as clients know them.</summary>
public static class ComponentKinds
{
    public static ComponentKind<PartCardProps, PartCardView> PartCard { get; } =
        new(BeaconComponents.PartCard, "part card", PartCardView.From);

    public static ComponentKind<ScheduleTimelineProps, ScheduleTimelineView> ScheduleTimeline { get; } =
        new(BeaconComponents.ScheduleTimeline, "schedule timeline", ScheduleTimelineView.From);

    public static ComponentKind<FaultHistoryChartProps, FaultHistoryChartView> FaultHistoryChart { get; } =
        new(BeaconComponents.FaultHistoryChart, "fault history chart", FaultHistoryChartView.From);

    public static IReadOnlyList<IComponentKind> All { get; } = [PartCard, ScheduleTimeline, FaultHistoryChart];
}
