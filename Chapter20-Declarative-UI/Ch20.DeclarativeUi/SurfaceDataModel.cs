using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Ch20.DeclarativeUi.JsonData;

namespace Ch20.DeclarativeUi;

/// <summary>
/// One surface's data model on the client. Components read from it through bindings; input components
/// write to it as the user types (A2UI's two-way binding), and nothing leaves the client until an action
/// sends the values its context names.
/// </summary>
public sealed class SurfaceDataModel(JsonObject initial)
{
    /// <summary>The current data. A copy of the surface's initial data, so edits never touch the document.</summary>
    public JsonObject Root { get; } = (JsonObject)initial.DeepClone();

    /// <summary>Incremented by every write, so a renderer can tell that bound values changed.</summary>
    public int Version { get; private set; }

    /// <summary>The pointer a property is bound to, or <see langword="null"/> for a literal.</summary>
    public static DataPointer? BindingOf(JsonNode? value) =>
        value is JsonObject { Count: 1 } binding && DataPointer.TryParse(Str(binding["path"]), out var pointer) ? pointer : null;

    public JsonNode? Read(DataPointer pointer) => TryGet(Root, pointer, out var value) ? value : null;

    /// <summary>A property's value: the literal itself, or what its binding points at in <paramref name="scope"/>.</summary>
    public JsonNode? Resolve(JsonNode? value, DataPointer scope) =>
        BindingOf(value) is { } pointer ? Read(pointer.In(scope)) : value;

    /// <summary>A property as text, with A2UI's conversions: null is empty, objects and arrays are JSON.</summary>
    public string Text(JsonNode? value, DataPointer scope) => ToText(Resolve(value, scope));

    public bool Boolean(JsonNode? value, DataPointer scope) =>
        Resolve(value, scope) is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    public double? Number(JsonNode? value, DataPointer scope) => ToNumber(Resolve(value, scope));

    public IReadOnlyList<string> StringList(JsonNode? value, DataPointer scope) =>
        Resolve(value, scope) is JsonArray list ? [.. list.Select(Str).OfType<string>()] : [];

    /// <summary>Writes through a property's binding. A literal property can't be written: it isn't the user's.</summary>
    public bool Write(JsonNode? property, DataPointer scope, JsonNode? value)
    {
        if (BindingOf(property) is not { } pointer || !TrySet(Root, pointer.In(scope), value))
        {
            return false;
        }

        Version++;
        return true;
    }

    /// <summary>Evaluates a check's condition with the catalog's pure functions. Anything else is false.</summary>
    public bool Check(JsonNode? condition, DataPointer scope) => condition switch
    {
        JsonValue literal => literal.GetValueKind() == JsonValueKind.True,
        JsonObject call when call.ContainsKey("call") => Call(call, scope),
        JsonObject binding => Boolean(binding, scope),
        _ => false,
    };

    /// <summary>An action's context with every binding resolved: what the client sends back to the agent.</summary>
    public JsonObject Context(JsonObject? context, DataPointer scope)
    {
        var resolved = new JsonObject();
        foreach (var (key, value) in context ?? new JsonObject())
        {
            resolved[key] = Resolve(value, scope)?.DeepClone();
        }

        return resolved;
    }

    public static string ToText(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValueKind() == JsonValueKind.True ? "true" : "false",
        _ => node.ToJsonString(),
    };

    public static double? ToNumber(JsonNode? node) => node switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<double>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.String
                         && double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private bool Call(JsonObject call, DataPointer scope)
    {
        var args = call["args"] as JsonObject;
        return Str(call["call"]) switch
        {
            "required" => IsPresent(Resolve(args?["value"], scope)),
            "numeric" => Number(args?["value"], scope) is { } n && Within(n, args),
            "length" => Within(Text(args?["value"], scope).Length, args),
            "and" => args?["values"] is JsonArray all && all.All(c => Check(c, scope)),
            "or" => args?["values"] is JsonArray any && any.Any(c => Check(c, scope)),
            "not" => !Check(args?["value"], scope),
            _ => false, // the validator refuses other functions; this never runs one
        };
    }

    private static bool Within(double value, JsonObject? args) =>
        (ToNumber(args?["min"]) is not { } min || value >= min) && (ToNumber(args?["max"]) is not { } max || value <= max);

    private static bool IsPresent(JsonNode? value) => value switch
    {
        null => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Trim().Length > 0,
        JsonArray list => list.Count > 0,
        JsonObject obj => obj.Count > 0,
        _ => true,
    };
}
