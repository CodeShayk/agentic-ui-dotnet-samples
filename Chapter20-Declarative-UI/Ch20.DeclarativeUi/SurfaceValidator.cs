using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Ch20.DeclarativeUi.JsonData;

namespace Ch20.DeclarativeUi;

/// <summary>
/// Checks one surface, as an array of A2UI v0.9 operations, against a catalog and a set of limits. The agent
/// runs it on what its model wrote before sending; a client runs it again on what arrives before drawing.
/// </summary>
/// <remarks>
/// The checks run cheapest and most dangerous first: size, then the shape of each operation, then each
/// component against the catalog, then the tree (root, references, cycles, depth), then the bindings.
/// Anything executable (a function the catalog doesn't list, a local <c>functionCall</c> action) is an error
/// in either mode, and so is a URL from an origin the policy doesn't allow.
/// </remarks>
public sealed partial class SurfaceValidator(SurfacePolicy policy)
{
    public SurfacePolicy Policy { get; } = policy;

    public SurfaceVerdict Validate(JsonNode? operations)
    {
        var run = new Run(Policy);
        var surface = run.Validate(operations);
        return run.Verdict(surface);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex ComponentId();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,79}$")]
    private static partial Regex SurfaceId();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex ActionName();

    private sealed class Run(SurfacePolicy policy)
    {
        private const int MaxCheckNesting = 4;
        private static readonly string[] PlaceholderProps = ["label", "text", "value"];
        private readonly List<SurfaceProblem> _problems = [];

        private SurfaceLimits Limits => policy.Limits;

        private bool Producer => policy.Mode == ValidationMode.Producer;

        private bool HasErrors => _problems.Any(p => p.IsError);

        public SurfaceVerdict Verdict(SurfaceDocument? surface)
        {
            var rejected = surface is null || HasErrors;
            var outcome = rejected ? SurfaceOutcome.Rejected : _problems.Count > 0 ? SurfaceOutcome.Degraded : SurfaceOutcome.Accepted;
            return new SurfaceVerdict(outcome, [.. _problems], rejected ? null : surface);
        }

        public SurfaceDocument? Validate(JsonNode? operations)
        {
            if (operations is null)
            {
                return Fail("empty_operations", string.Empty, "The surface has no operations.");
            }

            // Size first: never walk a payload that is too big to accept.
            var bytes = Encoding.UTF8.GetByteCount(operations.ToJsonString());
            if (bytes > Limits.MaxBytes)
            {
                return Fail("too_large", string.Empty, $"The surface is {bytes} bytes; the limit is {Limits.MaxBytes}.");
            }

            if (operations is not JsonArray ops || ops.Count == 0)
            {
                return Fail("empty_operations", string.Empty, "The surface is an array of A2UI operations, and this one is empty or not an array.");
            }

            if (ops.Count > Limits.MaxOperations)
            {
                return Fail("too_many_operations", string.Empty, $"The surface has {ops.Count} operations; the limit is {Limits.MaxOperations}.");
            }

            string? surfaceId = null;
            string? catalogId = null;
            var data = new JsonObject();
            List<(JsonObject Component, string Path, int Operation)> components = [];

            for (var i = 0; i < ops.Count; i++)
            {
                var path = $"/{i}";
                if (ops[i] is not JsonObject op)
                {
                    Error("invalid_operation", path, "Each operation is a JSON object.");
                    continue;
                }

                if (Str(op["version"]) is not { } version || !A2ui.AcceptedVersions.Contains(version))
                {
                    Error("unsupported_version", path + "/version", $"Beacon reads A2UI {string.Join(" and ", A2ui.AcceptedVersions)} operations.");
                    continue;
                }

                var bodies = op.Where(p => p.Key != "version").ToList();
                if (bodies.Count != 1 || bodies[0].Value is not JsonObject body)
                {
                    Error("invalid_operation", path, "An operation has a version and exactly one of createSurface, updateComponents, updateDataModel or deleteSurface.");
                    continue;
                }

                var kind = bodies[0].Key;
                path += "/" + kind;
                var id = Str(body["surfaceId"]);
                if (id is null || !SurfaceId().IsMatch(id))
                {
                    Error("invalid_surface_id", path + "/surfaceId", "A surface ID is up to 80 letters, digits or '-', '_', '.' and ':'.");
                    continue;
                }

                surfaceId ??= id;
                if (id != surfaceId)
                {
                    Error("surface_mismatch", path + "/surfaceId", $"One snapshot describes one surface, '{surfaceId}', not '{id}'.");
                    continue;
                }

                switch (kind)
                {
                    case "createSurface":
                        if (i != 0)
                        {
                            Error("create_not_first", path, "createSurface comes first, once.");
                        }

                        OnlyKeys(body, path, "surfaceId", "catalogId", "sendDataModel");
                        catalogId = Str(body["catalogId"]);
                        if (catalogId != policy.Catalog.Id)
                        {
                            Error("wrong_catalog", path + "/catalogId", $"This surface must use catalog {policy.Catalog.Id}.");
                        }

                        if (body["sendDataModel"] is { } send && send.GetValueKind() != JsonValueKind.False)
                        {
                            Error("send_data_model_unsupported", path + "/sendDataModel", "Data comes back to Beacon only through actions; sendDataModel must be false.");
                        }

                        break;

                    case "updateComponents":
                        OnlyKeys(body, path, "surfaceId", "components");
                        if (body["components"] is not JsonArray list || list.Count == 0)
                        {
                            Error("empty_components", path + "/components", "updateComponents needs a non-empty components array.");
                            break;
                        }

                        for (var c = 0; c < list.Count; c++)
                        {
                            if (list[c] is JsonObject component)
                            {
                                components.Add((component, $"{path}/components/{c}", i));
                            }
                            else
                            {
                                Error("invalid_component", $"{path}/components/{c}", "Each component is a JSON object.");
                            }
                        }

                        break;

                    case "updateDataModel":
                        OnlyKeys(body, path, "surfaceId", "path", "value");
                        ApplyData(data, body, path);
                        break;

                    case "deleteSurface":
                        Error("unsupported_operation", path, "Beacon replaces a surface with a new snapshot; it doesn't take deleteSurface.");
                        break;

                    default:
                        Error("invalid_operation", path, $"'{kind}' isn't an A2UI v0.9 operation.");
                        break;
                }
            }

            if (catalogId is null)
            {
                Error("no_create_surface", "/0", "The first operation is createSurface.");
            }

            if (components.Count == 0)
            {
                Error("empty_components", string.Empty, "The surface has no components.");
            }
            else if (components.Count > Limits.MaxComponents)
            {
                return Fail("too_many_components", string.Empty, $"The surface has {components.Count} components; the limit is {Limits.MaxComponents}.");
            }

            if (HasErrors)
            {
                return null;
            }

            // A later updateComponents may redefine a component, as in A2UI; one list can't define an ID twice.
            var nodes = new Dictionary<string, (ComponentNode Node, string Path)>(StringComparer.Ordinal);
            var seen = new HashSet<(int, string)>();
            foreach (var (component, path, operation) in components)
            {
                if (Component(component, path) is not { } node)
                {
                    continue;
                }

                if (!seen.Add((operation, node.Id)))
                {
                    Error("duplicate_id", path + "/id", $"Two components have the ID '{node.Id}'.");
                }

                nodes[node.Id] = (node, path);
            }

            if (!HasErrors)
            {
                Structure(nodes, data);
            }

            return HasErrors
                ? null
                : new SurfaceDocument(surfaceId!, catalogId!, nodes.ToDictionary(n => n.Key, n => n.Value.Node, StringComparer.Ordinal), data);
        }

        // ---------------------------------------------------------------- components

        private ComponentNode? Component(JsonObject component, string path)
        {
            var id = Str(component["id"]);
            if (id is null || !ComponentId().IsMatch(id))
            {
                Error(id is null ? "missing_id" : "invalid_id", path + "/id", "Every component has an ID of up to 64 letters, digits, '-' or '_'.");
                return null;
            }

            if (Str(component["component"]) is not { } type)
            {
                Error("missing_component_type", path + "/component", $"Component '{id}' has no type.");
                return null;
            }

            var inCatalog = policy.Catalog.TryGet(type, out var spec);
            if (!inCatalog || (policy.Renderable is { } renderable && !renderable.Contains(type)))
            {
                // Nothing in a component we can't draw is ever interpreted, but executable content is refused anyway.
                NoExecutableContent(component, path);
                if (Producer)
                {
                    Error("unknown_component", path + "/component", $"'{type}' isn't in catalog {policy.Catalog.Id}.");
                }
                else
                {
                    Warning("unknown_component", path + "/component",
                        inCatalog ? $"This client can't draw '{type}' yet and shows a placeholder." : $"'{type}' isn't in catalog {policy.Catalog.Id}; a placeholder is shown.");
                }

                return new ComponentNode(id, type, Placeholder(component), IsKnown: false);
            }

            var props = new JsonObject();
            foreach (var (name, value) in component)
            {
                var at = $"{path}/{name}";
                switch (name)
                {
                    case "id" or "component":
                        continue;
                    case "accessibility":
                        if (Accessibility(value, at))
                        {
                            props[name] = value!.DeepClone();
                        }

                        continue;
                    case "weight":
                        if (value is JsonValue weight && weight.GetValueKind() == JsonValueKind.Number)
                        {
                            props[name] = value.DeepClone();
                        }
                        else
                        {
                            Error("invalid_value", at, "weight is a number.");
                        }

                        continue;
                }

                if (!spec.Props.TryGetValue(name, out var prop))
                {
                    NoExecutableContent(value, at);
                    if (Producer)
                    {
                        Error("unknown_property", at, $"{type} has no property '{name}'.");
                    }
                    else
                    {
                        Warning("unknown_property", at, $"Ignored '{name}', which {type} doesn't have here.");
                    }

                    continue;
                }

                if (Property(prop, value, at))
                {
                    props[name] = value!.DeepClone();
                }
            }

            foreach (var required in spec.RequiredProps.Where(r => !component.ContainsKey(r)))
            {
                Error("missing_required_prop", $"{path}/{required}", $"{type} '{id}' needs '{required}'.");
            }

            return new ComponentNode(id, type, props, IsKnown: true);
        }

        // A placeholder shows what the component was about: its label or text, and a bound value if it has one.
        private static JsonObject Placeholder(JsonObject component)
        {
            var kept = new JsonObject();
            foreach (var name in PlaceholderProps)
            {
                if (component[name] is JsonValue value && value.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    kept[name] = value.DeepClone();
                }
                else if (component[name] is JsonObject { Count: 1 } binding && DataPointer.TryParse(Str(binding["path"]), out _))
                {
                    kept[name] = binding.DeepClone();
                }
            }

            return kept;
        }

        private bool Property(PropSpec prop, JsonNode? value, string at) => prop.Kind switch
        {
            PropKind.DynamicString => Dynamic(value, at, "a string", JsonValueKind.String),
            PropKind.DynamicBoolean => Dynamic(value, at, "true or false", JsonValueKind.True, JsonValueKind.False),
            PropKind.DynamicNumber => Dynamic(value, at, "a number", JsonValueKind.Number),
            PropKind.DynamicStringList => value is JsonArray list ? StringList(list, at) : Dynamic(value, at, "an array of strings"),
            PropKind.Number => Expect(value is JsonValue v && v.GetValueKind() == JsonValueKind.Number, at, "a number"),
            PropKind.Choice => Expect(Str(value) is { } choice && prop.Choices!.Contains(choice), at, "one of " + string.Join(", ", prop.Choices!)),
            PropKind.ComponentRef => Expect(Str(value) is { } child && ComponentId().IsMatch(child), at, "a component ID"),
            PropKind.ChildList => ChildList(value, at),
            PropKind.Options => Options(value, at),
            PropKind.Checks => Checks(value, at),
            PropKind.Action => Action(value, at),
            PropKind.Url => Url(value, at),
            _ => Expect(false, at, "a supported value"),
        };

        // A literal of the right kind, or a binding. Never a function call: Beacon allows those in checks only.
        private bool Dynamic(JsonNode? value, string at, string what, params JsonValueKind[] kinds)
        {
            switch (value)
            {
                case JsonObject obj when obj.ContainsKey("call"):
                    Error("function_not_allowed", at, "Beacon's catalog allows function calls only in checks.");
                    return false;
                case JsonObject obj:
                    return Binding(obj, at);
                case JsonValue literal when kinds.Contains(literal.GetValueKind()):
                    return literal.GetValueKind() != JsonValueKind.String || Length(literal.GetValue<string>(), at);
                default:
                    Error("invalid_value", at, $"Expected {what} or a binding.");
                    return false;
            }
        }

        private bool Binding(JsonObject obj, string at)
        {
            if (obj.Count == 1 && DataPointer.TryParse(Str(obj["path"]), out _))
            {
                return true;
            }

            Error("invalid_binding", at, "A binding is {\"path\": \"/a/json/pointer\"} and nothing else.");
            return false;
        }

        private bool StringList(JsonArray list, string at) =>
            Expect(list.Count <= Limits.MaxOptions && list.All(item => Str(item) is { } s && s.Length <= Limits.MaxStringLength), at, $"up to {Limits.MaxOptions} strings");

        private bool ChildList(JsonNode? value, string at)
        {
            switch (value)
            {
                case JsonArray ids:
                    if (ids.Count > Limits.MaxChildren)
                    {
                        Error("too_many_children", at, $"A container has at most {Limits.MaxChildren} children.");
                        return false;
                    }

                    return Expect(ids.All(i => Str(i) is { } id && ComponentId().IsMatch(id)), at, "an array of component IDs");

                case JsonObject template:
                    return Expect(
                        template.Count == 2 && Str(template["componentId"]) is { } t && ComponentId().IsMatch(t) && DataPointer.TryParse(Str(template["path"]), out _),
                        at, "{\"componentId\": ..., \"path\": ...}");

                default:
                    return Expect(false, at, "an array of component IDs or a template");
            }
        }

        private bool Options(JsonNode? value, string at)
        {
            if (value is not JsonArray options || options.Count == 0 || options.Count > Limits.MaxOptions)
            {
                return Expect(false, at, $"1 to {Limits.MaxOptions} options");
            }

            var ok = true;
            HashSet<string> values = [];
            for (var i = 0; i < options.Count; i++)
            {
                var item = $"{at}/{i}";
                if (options[i] is not JsonObject option || !OnlyKeys(option, item, "label", "value"))
                {
                    ok = Expect(false, item, "{\"label\": ..., \"value\": ...}");
                    continue;
                }

                ok &= Dynamic(option["label"], item + "/label", "a string", JsonValueKind.String);
                ok &= Expect(Str(option["value"]) is { Length: > 0 and <= 100 } v && values.Add(v), item + "/value", "a unique string");
            }

            return ok;
        }

        private bool Checks(JsonNode? value, string at)
        {
            if (value is not JsonArray checks || checks.Count > Limits.MaxChecks)
            {
                return Expect(false, at, $"up to {Limits.MaxChecks} checks");
            }

            var ok = true;
            for (var i = 0; i < checks.Count; i++)
            {
                var item = $"{at}/{i}";
                if (checks[i] is not JsonObject check || !OnlyKeys(check, item, "condition", "message"))
                {
                    ok = Expect(false, item, "{\"condition\": ..., \"message\": ...}");
                    continue;
                }

                ok &= Expect(Str(check["message"]) is { Length: > 0 and <= 200 }, item + "/message", "a message of up to 200 characters");
                ok &= Condition(check["condition"], item + "/condition", 0);
            }

            return ok;
        }

        // A check's condition: a Boolean, a binding or a call to one of the catalog's pure functions.
        private bool Condition(JsonNode? value, string at, int nesting)
        {
            if (nesting > MaxCheckNesting)
            {
                Error("check_too_deep", at, $"Checks nest at most {MaxCheckNesting} levels.");
                return false;
            }

            if (value is JsonValue literal && literal.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            {
                return true;
            }

            if (value is not JsonObject call)
            {
                return Expect(false, at, "a Boolean, a binding or a function call");
            }

            if (!call.ContainsKey("call"))
            {
                return Binding(call, at);
            }

            var name = Str(call["call"]);
            if (name is null || !policy.Catalog.Functions.Contains(name))
            {
                Error("function_not_allowed", at + "/call", $"'{name}' isn't a function Beacon's renderers run.");
                return false;
            }

            if (!OnlyKeys(call, at, "call", "args", "returnType")
                || (call["returnType"] is { } returnType && Str(returnType) != "boolean")
                || call["args"] is not JsonObject args)
            {
                return Expect(false, at, "{\"call\": ..., \"args\": {...}}");
            }

            var a = at + "/args";
            return name switch
            {
                "required" => OnlyKeys(args, a, "value") && Operand(args["value"], a + "/value"),
                "numeric" => OnlyKeys(args, a, "value", "min", "max") && Operand(args["value"], a + "/value")
                             && Bounds(args, a),
                "length" => OnlyKeys(args, a, "value", "min", "max") && Operand(args["value"], a + "/value")
                            && Bounds(args, a),
                "and" or "or" => OnlyKeys(args, a, "values")
                                 && (args["values"] is JsonArray { Count: >= 2 and <= 8 } values
                                     ? values.Select((v, i) => Condition(v, $"{a}/values/{i}", nesting + 1)).ToList().All(ok => ok)
                                     : Expect(false, a + "/values", "2 to 8 conditions")),
                "not" => OnlyKeys(args, a, "value") && Condition(args["value"], a + "/value", nesting + 1),
                _ => Expect(false, at, "a known function"),
            };
        }

        private bool Operand(JsonNode? value, string at) => value switch
        {
            JsonObject obj => Binding(obj, at),
            JsonValue literal when literal.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
            _ => Expect(false, at, "a literal or a binding"),
        };

        private bool Bounds(JsonObject args, string at) =>
            Expect(args.ContainsKey("min") || args.ContainsKey("max"), at, "a min or a max")
            && Expect(new[] { args["min"], args["max"] }.All(b => b is null || (b is JsonValue v && v.GetValueKind() == JsonValueKind.Number)), at, "numeric bounds");

        // Actions only send a named event, with context, to the agent. A local functionCall is refused.
        private bool Action(JsonNode? value, string at)
        {
            if (value is JsonObject local && local.ContainsKey("functionCall"))
            {
                Error("local_action_not_allowed", at + "/functionCall", "Actions only send events to Beacon; local function calls aren't allowed.");
                return false;
            }

            if (value is not JsonObject action || !OnlyKeys(action, at, "event") || action["event"] is not JsonObject evt || !OnlyKeys(evt, at + "/event", "name", "context"))
            {
                return Expect(false, at, "{\"event\": {\"name\": ..., \"context\": {...}}}");
            }

            var ok = Expect(Str(evt["name"]) is { } name && ActionName().IsMatch(name), at + "/event/name", "an action name of lowercase letters, digits and '_'");
            if (evt["context"] is null)
            {
                return ok;
            }

            if (evt["context"] is not JsonObject context || context.Count > Limits.MaxContextEntries)
            {
                return Expect(false, at + "/event/context", $"an object of up to {Limits.MaxContextEntries} entries");
            }

            foreach (var (key, entry) in context)
            {
                var item = $"{at}/event/context/{key}";
                ok &= entry switch
                {
                    JsonObject obj when obj.ContainsKey("call") => Expect(false, item, "a literal or a binding, not a function call"),
                    JsonObject obj => Binding(obj, item),
                    JsonArray list => Expect(list.All(i => i is JsonValue), item, "an array of literals"),
                    JsonValue literal => literal.GetValueKind() != JsonValueKind.String || Length(literal.GetValue<string>(), item),
                    _ => Expect(false, item, "a literal or a binding"),
                };
            }

            return ok;
        }

        // URLs are literal, absolute https and from an allowed origin. A binding could change one after this check.
        private bool Url(JsonNode? value, string at)
        {
            if (value is JsonObject)
            {
                Error("bound_url_not_allowed", at, "A URL must be written in the component, not bound to data.");
                return false;
            }

            if (Str(value) is not { } url || !policy.Urls.IsAllowed(url))
            {
                Error("url_not_allowed", at, "Images and links come only from Beacon's allowed https origins.");
                return false;
            }

            return true;
        }

        private bool Accessibility(JsonNode? value, string at) =>
            value is JsonObject a && OnlyKeys(a, at, "label", "description")
                ? a.All(p => Dynamic(p.Value, $"{at}/{p.Key}", "a string", JsonValueKind.String))
                : Expect(false, at, "{\"label\": ..., \"description\": ...}");

        // Refuses executable content anywhere in a subtree this validator otherwise wouldn't interpret.
        private void NoExecutableContent(JsonNode? node, string at)
        {
            Stack<(JsonNode? Node, string Path)> pending = new([(node, at)]);
            while (pending.TryPop(out var item))
            {
                switch (item.Node)
                {
                    case JsonObject obj:
                        if (obj.ContainsKey("functionCall"))
                        {
                            Error("local_action_not_allowed", item.Path + "/functionCall", "Local function calls aren't allowed.");
                        }

                        if (obj.ContainsKey("call") && (Str(obj["call"]) is not { } name || !policy.Catalog.Functions.Contains(name)))
                        {
                            Error("function_not_allowed", item.Path + "/call", "Only the catalog's check functions may be called.");
                        }

                        foreach (var (key, child) in obj)
                        {
                            pending.Push((child, $"{item.Path}/{key}"));
                        }

                        break;
                    case JsonArray array:
                        for (var i = 0; i < array.Count; i++)
                        {
                            pending.Push((array[i], $"{item.Path}/{i}"));
                        }

                        break;
                }
            }
        }

        // ---------------------------------------------------------------- the tree

        private void Structure(Dictionary<string, (ComponentNode Node, string Path)> nodes, JsonObject data)
        {
            if (!nodes.ContainsKey("root"))
            {
                Error("no_root", "/components", "No component has the ID 'root'.");
                return;
            }

            // The edges: every child a component this renderer understands refers to.
            var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (id, (node, path)) in nodes)
            {
                List<string> children = [];
                edges[id] = children;
                if (!node.IsKnown)
                {
                    continue;
                }

                foreach (var prop in policy.Catalog.Components[node.Type].ChildProps)
                {
                    foreach (var (child, at) in ChildrenOf(node.Properties[prop], $"{path}/{prop}"))
                    {
                        if (nodes.ContainsKey(child))
                        {
                            children.Add(child);
                        }
                        else
                        {
                            Error("unresolved_child", at, $"'{id}' refers to '{child}', which no component defines.");
                        }
                    }
                }
            }

            if (HasErrors)
            {
                return;
            }

            if (FindCycle(edges) is { } cycle)
            {
                Error("child_cycle", nodes[cycle[0]].Path, $"Components refer to each other in a loop: {string.Join(" -> ", cycle)} -> {cycle[0]}.");
                return;
            }

            if (Depth("root", edges) is var depth && depth > Limits.MaxDepth)
            {
                Error("too_deep", nodes["root"].Path, $"The tree is {depth} levels deep; the limit is {Limits.MaxDepth}.");
                return;
            }

            if (Producer)
            {
                foreach (var (node, path) in nodes.Values)
                {
                    Bindings(node.Properties, path, data);
                }
            }
        }

        private static IEnumerable<(string Child, string At)> ChildrenOf(JsonNode? value, string at)
        {
            switch (value)
            {
                case JsonValue when Str(value) is { } single:
                    yield return (single, at);
                    break;
                case JsonArray list:
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (Str(list[i]) is { } child)
                        {
                            yield return (child, $"{at}/{i}");
                        }
                    }

                    break;
                case JsonObject template when Str(template["componentId"]) is { } templateId:
                    yield return (templateId, at + "/componentId");
                    break;
            }
        }

        // An iterative depth-first search, so a pathological chain can't overflow the stack.
        private static List<string>? FindCycle(Dictionary<string, List<string>> edges)
        {
            var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = on the path, 2 = done
            foreach (var start in edges.Keys)
            {
                if (state.ContainsKey(start))
                {
                    continue;
                }

                var path = new List<string> { start };
                var stack = new Stack<(string Node, int Next)>([(start, 0)]);
                state[start] = 1;
                while (stack.TryPop(out var frame))
                {
                    var children = edges[frame.Node];
                    if (frame.Next < children.Count)
                    {
                        stack.Push((frame.Node, frame.Next + 1));
                        var child = children[frame.Next];
                        if (state.TryGetValue(child, out var s) && s == 1)
                        {
                            return path[path.IndexOf(child)..];
                        }

                        if (!state.ContainsKey(child))
                        {
                            state[child] = 1;
                            path.Add(child);
                            stack.Push((child, 0));
                        }
                    }
                    else
                    {
                        state[frame.Node] = 2;
                        path.RemoveAt(path.Count - 1);
                    }
                }
            }

            return null;
        }

        // The longest chain below a component, counting it. The graph has no cycles by now.
        private static int Depth(string id, Dictionary<string, List<string>> edges)
        {
            var memo = new Dictionary<string, int>(StringComparer.Ordinal);
            int Measure(string node) =>
                memo.TryGetValue(node, out var known) ? known : memo[node] = 1 + edges[node].Select(Measure).DefaultIfEmpty(0).Max();
            return Measure(id);
        }

        // Every absolute binding must point at something the data model already has.
        private void Bindings(JsonNode? node, string at, JsonObject data)
        {
            switch (node)
            {
                case JsonObject { Count: 1 } binding when Str(binding["path"]) is { } path && DataPointer.TryParse(path, out var pointer):
                    if (pointer.IsAbsolute && !TryGet(data, pointer, out _))
                    {
                        Error("unresolved_binding", at, $"'{path}' isn't in the surface's data model.");
                    }

                    break;
                case JsonObject template when template.ContainsKey("componentId") && DataPointer.TryParse(Str(template["path"]), out var items):
                    if (items.IsAbsolute && !(TryGet(data, items, out var list) && list is JsonArray))
                    {
                        Error("unresolved_binding", at + "/path", "A template repeats over an array in the data model.");
                    }

                    break;
                case JsonObject obj:
                    foreach (var (name, child) in obj)
                    {
                        Bindings(child, $"{at}/{name}", data);
                    }

                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        Bindings(array[i], $"{at}/{i}", data);
                    }

                    break;
            }
        }

        // ---------------------------------------------------------------- the data model

        private void ApplyData(JsonObject data, JsonObject body, string at)
        {
            var path = Str(body["path"]) ?? "/";
            if (!DataPointer.TryParse(path, out var pointer) || !pointer.IsAbsolute)
            {
                Error("invalid_pointer", at + "/path", "updateDataModel takes an absolute JSON Pointer.");
                return;
            }

            var applied = body.TryGetPropertyValue("value", out var value)
                ? TrySet(data, pointer, value?.DeepClone())
                : TryRemove(data, pointer);
            if (!applied)
            {
                Error("invalid_pointer", at + "/path", pointer.Segments.Count == 0 ? "The whole data model is replaced only by an object." : $"'{path}' can't be written.");
            }
        }

        // ---------------------------------------------------------------- helpers

        private bool OnlyKeys(JsonObject obj, string at, params string[] allowed)
        {
            var extra = obj.Select(p => p.Key).Where(k => !allowed.Contains(k)).ToList();
            foreach (var key in extra)
            {
                Error("unknown_property", $"{at}/{key}", $"'{key}' isn't allowed here.");
            }

            return extra.Count == 0;
        }

        private bool Length(string text, string at) =>
            Expect(text.Length <= Limits.MaxStringLength, at, $"at most {Limits.MaxStringLength} characters");

        private bool Expect(bool condition, string at, string what)
        {
            if (!condition)
            {
                Error("invalid_value", at, $"Expected {what}.");
            }

            return condition;
        }

        private SurfaceDocument? Fail(string code, string at, string message)
        {
            Error(code, at, message);
            return null;
        }

        private void Error(string code, string at, string message) => _problems.Add(new SurfaceProblem(code, at, message, IsError: true));

        private void Warning(string code, string at, string message) => _problems.Add(new SurfaceProblem(code, at, message, IsError: false));
    }
}
