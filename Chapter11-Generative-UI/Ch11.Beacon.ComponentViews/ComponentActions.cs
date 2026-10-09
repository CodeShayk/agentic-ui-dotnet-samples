namespace Ch11.Beacon.ComponentViews;

/// <summary>The user chose an action on a generated component, such as Order this part on a part card.</summary>
/// <param name="Component">The component, as it was rendered when the user chose.</param>
/// <param name="ActionName">The action's name, from the server's <c>ComponentAction</c>.</param>
public sealed record ComponentActionRequest(GeneratedComponent Component, string ActionName);

/// <summary>
/// What an action on a generated component says to the agent. The answer is a user message: the
/// component's own call was answered by the server, and the user, not the agent, is acting now.
/// </summary>
public static class ComponentActionMessages
{
    /// <summary>
    /// The message for an action, or <see langword="null"/> when it mustn't be sent: the component isn't
    /// ready (its props are still a preview, or the server hasn't accepted them), or the server didn't
    /// grant the action.
    /// </summary>
    /// <remarks>
    /// The text is built from the server's accepted props, never from the streamed arguments, and reads
    /// the way Dana would type it: it appears in the transcript, and a real model handles it like any request.
    /// </remarks>
    public static string? Compose(ComponentActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var component = request.Component;
        if (component.Phase != ComponentPhase.Ready || !component.Actions.Any(a => a.Name == request.ActionName && a.Enabled))
        {
            return null;
        }

        return (component.ComponentName, request.ActionName, component.View) switch
        {
            ("PartCard", "order", PartCardView { Sku: { } sku, WorkOrderId: { } workOrderId }) => $"Order one {sku} for {workOrderId}.",
            _ => null,
        };
    }
}
