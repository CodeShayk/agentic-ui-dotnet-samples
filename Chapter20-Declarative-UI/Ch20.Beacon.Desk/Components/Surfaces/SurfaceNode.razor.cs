using System.Text.Json;
using System.Text.Json.Nodes;
using Ch20.DeclarativeUi;
using Microsoft.AspNetCore.Components;

namespace Ch20.Beacon.Desk.Components.Surfaces;

/// <summary>
/// Draws one node of a surface and, recursively, its children. Reads go through the surface's data model
/// (literals and bindings alike), writes through <see cref="SurfaceInstance.Write"/>, and a button press is
/// handed up as <see cref="OnAction"/>; the node itself never talks to the agent.
/// </summary>
public sealed partial class SurfaceNode
{
    [Parameter, EditorRequired] public RenderNode Node { get; set; } = default!;

    [Parameter, EditorRequired] public SurfaceInstance Surface { get; set; } = default!;

    /// <summary>Raised after an input wrote to the data model, so the surface re-renders everything bound to it.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    [Parameter] public EventCallback<RenderNode> OnAction { get; set; }

    /// <summary>A run is streaming, so the Desk can't send an action yet; buttons wait and say so.</summary>
    [Parameter] public bool Busy { get; set; }

    private SurfaceDataModel Data => Surface.Data!;

    private string Id => Node.DomId(Surface.SurfaceId);

    private IReadOnlyList<string> Failed => Surface.IsTouched(Node) ? Surface.FailedChecks(Node) : [];

    private string? Invalid => Failed.Count > 0 ? "true" : null;

    private string? Described => Failed.Count > 0 ? $"{Id}-error" : null;

    // The first failed check, shown once the user has touched the field, and tied to it by aria-describedby.
    private RenderFragment Problems => builder =>
    {
        if (Failed.Count > 0)
        {
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "id", $"{Id}-error");
            builder.AddAttribute(2, "class", "ch20-error");
            builder.AddContent(3, Failed[0]);
            builder.CloseElement();
        }
    };

    // The children, each a SurfaceNode with the same surface and callbacks, keyed so inputs keep focus.
    private RenderFragment ChildNodes => builder =>
    {
        foreach (var child in Node.Children)
        {
            builder.OpenComponent<SurfaceNode>(0);
            builder.SetKey(child.Key);
            builder.AddComponentParameter(1, nameof(Node), child);
            builder.AddComponentParameter(2, nameof(Surface), Surface);
            builder.AddComponentParameter(3, nameof(OnChanged), OnChanged);
            builder.AddComponentParameter(4, nameof(OnAction), OnAction);
            builder.AddComponentParameter(5, nameof(Busy), Busy);
            builder.CloseComponent();
        }
    };

    private string Text(string property, string? fallback = null) =>
        Node[property] is null ? fallback ?? string.Empty : Data.Text(Node[property], Node.Scope);

    private string? Choice(string property) =>
        Node[property] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private string? Literal(string property) => Choice(property);

    // Layout hints come from enumerations the validator checked, so they can safely become class names.
    private string Layout() => $"ch20-justify-{Choice("justify") ?? "start"} ch20-align-{Choice("align") ?? "stretch"}";

    private IEnumerable<(string Label, string Value)> Options() =>
        (Node["options"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => (Data.Text(o["label"], Node.Scope), o["value"]?.GetValue<string>() ?? string.Empty));

    private static JsonArray Toggle(IReadOnlyList<string> selected, string value, bool single) =>
        single ? [JsonValue.Create(value)]
        : selected.Contains(value) ? [.. selected.Where(v => v != value).Select(v => (JsonNode?)JsonValue.Create(v))]
        : [.. selected.Append(value).Select(v => (JsonNode?)JsonValue.Create(v))];

    private async Task WriteAsync(JsonNode? value)
    {
        if (Surface.Write(Node, "value", value))
        {
            await OnChanged.InvokeAsync();
        }
    }
}
