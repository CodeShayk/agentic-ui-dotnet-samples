using System.Diagnostics.CodeAnalysis;
using Ch11.Beacon.ComponentViews;
using Microsoft.AspNetCore.Components;

namespace Ch11.Beacon.Desk.Components.Generative;

/// <summary>
/// The components this Desk can render for the agent, by tool name: each entry pairs a component kind
/// (its contract, prop schema and view model) with the Blazor component that draws it.
/// <see cref="GeneratedComponentHost"/> looks entries up and renders them with <see cref="DynamicComponent"/>.
/// </summary>
/// <remarks>
/// A component must derive from <see cref="GeneratedComponentBase{TView}"/> for the kind's view model, so
/// every entry takes the same parameters with the right types; <see cref="Register{TComponent, TView}"/>
/// checks this when the registry is built, instead of <see cref="DynamicComponent"/> failing at render time.
/// </remarks>
public sealed class ComponentRegistry
{
    private readonly Dictionary<string, ComponentRegistration> _byTool = new(StringComparer.Ordinal);

    /// <summary>Beacon Desk's registry: the three catalog components.</summary>
    public static ComponentRegistry CreateDefault() => new ComponentRegistry()
        .Register<PartCard, PartCardView>(ComponentKinds.PartCard)
        .Register<ScheduleTimeline, ScheduleTimelineView>(ComponentKinds.ScheduleTimeline)
        .Register<FaultHistoryChart, FaultHistoryChartView>(ComponentKinds.FaultHistoryChart);

    /// <summary>Maps a component kind to the Blazor component that renders its view model.</summary>
    public ComponentRegistry Register<TComponent, TView>(IComponentKind kind)
        where TComponent : GeneratedComponentBase<TView>
        where TView : class, IComponentView
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (kind.ViewType != typeof(TView))
        {
            throw new ArgumentException(
                $"{typeof(TComponent).Name} renders {typeof(TView).Name}, but {kind.Contract.ComponentName} produces {kind.ViewType.Name}.", nameof(kind));
        }

        _byTool.Add(kind.Contract.ToolName, new ComponentRegistration(kind, typeof(TComponent)));
        return this;
    }

    public IReadOnlyCollection<ComponentRegistration> Registrations => _byTool.Values;

    public bool TryResolve(string toolName, [NotNullWhen(true)] out ComponentRegistration? registration) =>
        _byTool.TryGetValue(toolName, out registration);

    /// <summary>A projector that knows exactly the components this registry can render.</summary>
    public ComponentProjector CreateProjector() => new(_byTool.Values.Select(r => r.Kind));
}

/// <summary>One registry entry: the kind and the Blazor component type.</summary>
public sealed record ComponentRegistration(IComponentKind Kind, Type ComponentType)
{
    /// <summary>The parameters <see cref="DynamicComponent"/> passes, named as <see cref="GeneratedComponentBase{TView}"/> declares them.</summary>
    public IDictionary<string, object> ParametersFor(GeneratedComponent component, EventCallback<ComponentActionRequest> onAction) =>
        new Dictionary<string, object>
        {
            [nameof(GeneratedComponentBase<PartCardView>.View)] = component.View ?? throw new ArgumentException("The component has no view to render.", nameof(component)),
            [nameof(GeneratedComponentBase<PartCardView>.Component)] = component,
            [nameof(GeneratedComponentBase<PartCardView>.OnAction)] = onAction,
        };
}
