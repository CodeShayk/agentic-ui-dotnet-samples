using System.Text.Json;
using Ch11.Beacon.Components;

namespace Ch11.Beacon.ComponentViews;

/// <summary>Where a generated component is in its life, as the user should see it.</summary>
public enum ComponentPhase
{
    /// <summary>Arguments are streaming: render a draft of what has arrived, with nothing interactive.</summary>
    Drafting,

    /// <summary>The arguments are complete (<c>TOOL_CALL_END</c>) and the server is checking them.</summary>
    Validating,

    /// <summary>The server accepted the props and this client validated them: render the component.</summary>
    Ready,

    /// <summary>The server or this client refused the props, or the component is unknown: render a fallback.</summary>
    Rejected,

    /// <summary>The turn stopped or failed before the component was settled.</summary>
    Abandoned,
}

/// <summary>A view model that can describe itself in one sentence, for screen readers and fallbacks.</summary>
public interface IComponentView
{
    /// <summary>What the component shows, as plain text.</summary>
    string Summary { get; }
}

/// <summary>
/// One component the agent asked for, projected from its tool call: what to render, how settled it is
/// and what the user may do with it. Immutable; every change in the call produces a new one.
/// </summary>
/// <param name="CallId">The tool call's ID.</param>
/// <param name="ToolName">The tool the model called, such as <c>show_part_card</c>.</param>
public sealed record GeneratedComponent(string CallId, string ToolName)
{
    /// <summary>The component this client renders for the call; <see langword="null"/> when it doesn't know one.</summary>
    public string? ComponentName { get; init; }

    /// <summary>A short name for the component in sentences, such as "part card".</summary>
    public string DisplayName { get; init; } = "view";

    public ComponentPhase Phase { get; init; }

    /// <summary>The view model to render (a <see cref="PartCardView"/>, say), or <see langword="null"/> when nothing renderable has arrived.</summary>
    public IComponentView? View { get; init; }

    /// <summary>The props the view was built from: a draft while streaming, the server's version once accepted.</summary>
    public JsonElement? Props { get; init; }

    /// <summary>The interactions the server allows. Empty until <see cref="ComponentPhase.Ready"/>.</summary>
    public IReadOnlyList<ComponentAction> Actions { get; init; } = [];

    /// <summary>Why the component can't be shown, in the server's or this client's words.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>What the server corrected in the model's props.</summary>
    public IReadOnlyList<string> Corrections { get; init; } = [];

    /// <summary>How much argument text has arrived, in characters.</summary>
    public int ArgumentsLength { get; init; }

    public bool IsKnown => ComponentName is not null;

    /// <summary>True while the props are still a preview: nothing may act on them.</summary>
    public bool IsDraft => Phase is ComponentPhase.Drafting or ComponentPhase.Validating;

    /// <summary>One sentence about the component in its current phase.</summary>
    public string Describe() => Phase switch
    {
        ComponentPhase.Ready when IsKnown && View is not null => View.Summary,
        ComponentPhase.Drafting or ComponentPhase.Validating => $"Beacon is preparing a {DisplayName}.",
        ComponentPhase.Abandoned => $"The {DisplayName} stopped before it was finished.",
        _ => $"Beacon couldn't show this {DisplayName}{(Problems is [var first, ..] ? $": {Plain(first)}" : ".")}",
    };

    /// <summary>The same component with one action marked as taken, so it can't be sent twice.</summary>
    public GeneratedComponent WithActionTaken(string actionName) => this with
    {
        Actions = [.. Actions.Select(a => a.Name == actionName ? a with { Enabled = false, Reason = "Requested" } : a)],
    };

    // Problems carry a location for the model ("$.sku: ..."); people don't need it.
    public static string Plain(string problem) =>
        problem.StartsWith('$') && problem.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0 ? problem[(colon + 2)..] : problem;
}
