using Ch11.Beacon.Components;
using Ch11.Beacon.ComponentViews;
using Microsoft.AspNetCore.Components;

namespace Ch11.Beacon.Desk.Components.Generative;

/// <summary>
/// The base of every component the agent can show on the Desk: a typed view model, the generated
/// component it belongs to (phase, actions, corrections) and a callback for the user's actions.
/// </summary>
/// <typeparam name="TView">The view model the component draws.</typeparam>
public abstract class GeneratedComponentBase<TView> : ComponentBase
    where TView : class, IComponentView
{
    /// <summary>What to draw. While <see cref="IsDraft"/>, any field may still be missing.</summary>
    [Parameter, EditorRequired]
    public TView View { get; set; } = default!;

    [Parameter, EditorRequired]
    public GeneratedComponent Component { get; set; } = default!;

    [Parameter]
    public EventCallback<ComponentActionRequest> OnAction { get; set; }

    /// <summary>True while the props are a preview: draw placeholders and nothing interactive.</summary>
    protected bool IsDraft => Component.IsDraft;

    /// <summary>The server's actions, when the component is ready; none while it's a draft.</summary>
    protected IReadOnlyList<ComponentAction> Actions => Component.Phase == ComponentPhase.Ready ? Component.Actions : [];

    protected Task RequestAsync(ComponentAction action) =>
        action.Enabled ? OnAction.InvokeAsync(new ComponentActionRequest(Component, action.Name)) : Task.CompletedTask;
}
