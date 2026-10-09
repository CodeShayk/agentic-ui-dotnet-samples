using System.Text.Json;
using Beacon.Client.Core;
using Ch11.Beacon.Components;

namespace Ch11.Beacon.ComponentViews;

/// <summary>
/// Turns the conversation's tool calls into generated components, for the components this client knows.
/// It reads only <see cref="ToolCallView"/>, Beacon.Client.Core's tool-call slice: the argument text that
/// a <see cref="WireEventTap"/> fills while it streams, the status and the server's result.
/// </summary>
/// <remarks>
/// One projector per conversation. It remembers the last projection of each call, so rendering the same
/// view twice costs nothing, and so a draft keeps its last good preview when the next fragment can't be
/// read yet. Call <see cref="Clear"/> when the conversation is reset.
/// </remarks>
public sealed class ComponentProjector
{
    private readonly Dictionary<string, IComponentKind> _kinds;
    private readonly Dictionary<string, (ToolCallView Call, GeneratedComponent Component)> _projected = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public ComponentProjector(IEnumerable<IComponentKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        _kinds = kinds.ToDictionary(k => k.Contract.ToolName, StringComparer.Ordinal);
    }

    /// <summary>Whether this client has a component for a tool name.</summary>
    public bool Knows(string toolName) => _kinds.ContainsKey(toolName);

    /// <summary>
    /// The component a tool call stands for, or <see langword="null"/> when the call is an ordinary tool
    /// call (a lookup, an order) that the UI should show as activity instead.
    /// </summary>
    public GeneratedComponent? Project(ToolCallView call)
    {
        ArgumentNullException.ThrowIfNull(call);
        lock (_gate)
        {
            _projected.TryGetValue(call.Id, out var previous);
            if (ReferenceEquals(previous.Call, call))
            {
                return previous.Component; // the reducer replaces a call whenever it changes
            }

            var component = ProjectCore(call, previous.Component);
            if (component is not null)
            {
                _projected[call.Id] = (call, component);
            }

            return component;
        }
    }

    /// <summary>Forgets every projection, for a new conversation.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _projected.Clear();
        }
    }

    private GeneratedComponent? ProjectCore(ToolCallView call, GeneratedComponent? previous)
    {
        if (!_kinds.TryGetValue(call.Name, out var kind))
        {
            // Not a component this client knows. It was meant as one if the server answered it with an
            // envelope: a component this build doesn't have, or a name the server refused. Otherwise it's
            // an ordinary tool call.
            return ComponentResult.TryParse(call.Result, out var envelope) ? Unknown(call, envelope) : null;
        }

        var component = new GeneratedComponent(call.Id, call.Name)
        {
            ComponentName = kind.Contract.ComponentName,
            DisplayName = kind.DisplayName,
            ArgumentsLength = call.ArgumentsText.Length,
        };

        return call.Status switch
        {
            ToolCallStatus.Streaming => Draft(component with { Phase = ComponentPhase.Drafting }, kind, PartialJson.TryComplete(call.ArgumentsText), previous),
            ToolCallStatus.InProgress => Draft(component with { Phase = ComponentPhase.Validating }, kind, call.Arguments ?? PartialJson.TryComplete(call.ArgumentsText), previous),
            ToolCallStatus.Completed => Settle(component, kind, call.Result),
            ToolCallStatus.Abandoned => component with { Phase = ComponentPhase.Abandoned, View = previous?.View, Props = previous?.Props },
            _ => Reject(component, "Beacon's agent didn't confirm this view."), // a component call is never the client's to answer
        };
    }

    // A preview: whatever the arguments so far can show. A fragment that can't be read yet keeps the last good draft.
    private static GeneratedComponent Draft(GeneratedComponent component, IComponentKind kind, JsonElement? props, GeneratedComponent? previous)
    {
        var view = props is { } p ? kind.TryCreateView(p) : null;
        return view is not null
            ? component with { Props = props, View = view }
            : component with { Props = previous?.Props, View = previous?.View };
    }

    // The server's verdict decides, and this client checks the props it was given before trusting them:
    // the server could be another build, or another server altogether.
    private static GeneratedComponent Settle(GeneratedComponent component, IComponentKind kind, string? result)
    {
        if (!ComponentResult.TryParse(result, out var envelope))
        {
            return Reject(component, "The agent's answer for this view couldn't be read.");
        }

        if (!envelope.IsAccepted || envelope.Props is not { } props)
        {
            return Reject(component, envelope.Errors is { Count: > 0 } errors ? [.. errors] : ["The agent didn't accept this view."]);
        }

        if (kind.Contract.Validate(props) is { Count: > 0 } problems)
        {
            return Reject(component, ["The props the agent confirmed don't match this component's schema.", .. problems]);
        }

        return kind.TryCreateView(props) is { } view
            ? component with
            {
                Phase = ComponentPhase.Ready,
                Props = props,
                View = view,
                Actions = envelope.Actions ?? [],
                Corrections = envelope.Corrections ?? [],
            }
            : Reject(component, "The props the agent confirmed couldn't be read.");
    }

    private static GeneratedComponent Unknown(ToolCallView call, ComponentResult envelope) =>
        new(call.Id, call.Name)
        {
            Phase = ComponentPhase.Rejected,
            Props = envelope.Props,
            ArgumentsLength = call.ArgumentsText.Length,
            Problems = envelope.IsAccepted
                ? [$"This version of the app has no {envelope.Component ?? call.Name} view."]
                : envelope.Errors is { Count: > 0 } errors ? [.. errors] : ["The agent refused this view."],
        };

    private static GeneratedComponent Reject(GeneratedComponent component, params IReadOnlyList<string> problems) =>
        component with { Phase = ComponentPhase.Rejected, Problems = problems, View = null };
}
