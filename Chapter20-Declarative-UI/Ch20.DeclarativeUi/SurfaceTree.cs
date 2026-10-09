using System.Globalization;
using System.Text.Json.Nodes;
using static Ch20.DeclarativeUi.JsonData;

namespace Ch20.DeclarativeUi;

/// <summary>
/// One node to draw: a component, the data scope its relative bindings resolve in, and its children. A
/// template repeated over an array becomes one node per item, each with that item as its scope.
/// </summary>
/// <param name="Key">Stable across renders: the path of IDs (and item indexes) from the root.</param>
public sealed record RenderNode(string Key, ComponentNode Component, DataPointer Scope, IReadOnlyList<RenderNode> Children)
{
    public string Type => Component.Type;

    public bool IsKnown => Component.IsKnown;

    public JsonNode? this[string property] => Component.Properties[property];

    /// <summary>An element ID for this node, unique within <paramref name="surfaceId"/>'s surface.</summary>
    public string DomId(string surfaceId) =>
        "ch20-" + string.Concat($"{surfaceId}-{Key}".Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}

/// <summary>Turns a validated surface and its current data into the tree a renderer walks.</summary>
public static class SurfaceTree
{
    public static RenderNode? Build(SurfaceDocument document, SurfaceDataModel data, ComponentCatalog catalog, SurfaceLimits limits)
    {
        var budget = limits.MaxRenderedNodes;

        RenderNode? Node(string id, string key, DataPointer scope, int depth)
        {
            // The validator has ruled out cycles and deep trees; the budget also bounds what templates expand to.
            if (--budget < 0 || depth > limits.MaxDepth || !document.Components.TryGetValue(id, out var component))
            {
                return null;
            }

            List<RenderNode> children = [];
            if (component.IsKnown && catalog.TryGet(component.Type, out var spec))
            {
                foreach (var prop in spec.ChildProps)
                {
                    switch (component.Properties[prop])
                    {
                        case JsonValue single when Str(single) is { } childId:
                            Add(Node(childId, $"{key}/{childId}", scope, depth + 1));
                            break;

                        case JsonArray ids:
                            foreach (var childId in ids.Select(Str).OfType<string>())
                            {
                                Add(Node(childId, $"{key}/{childId}", scope, depth + 1));
                            }

                            break;

                        case JsonObject template when Str(template["componentId"]) is { } templateId
                                                      && DataPointer.TryParse(Str(template["path"]), out var path):
                            var items = path.In(scope);
                            if (data.Read(items) is JsonArray list)
                            {
                                for (var i = 0; i < list.Count; i++)
                                {
                                    var index = i.ToString(CultureInfo.InvariantCulture);
                                    Add(Node(templateId, $"{key}/{templateId}@{index}", items.Append(index), depth + 1));
                                }
                            }

                            break;
                    }
                }
            }

            return new RenderNode(key, component, scope, children);

            void Add(RenderNode? child)
            {
                if (child is not null)
                {
                    children.Add(child);
                }
            }
        }

        return Node("root", "root", DataPointer.Root, 1);
    }
}
