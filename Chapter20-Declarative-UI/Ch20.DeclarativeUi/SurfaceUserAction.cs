using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Ch20.DeclarativeUi.JsonData;

namespace Ch20.DeclarativeUi;

/// <summary>
/// A user action on a surface: A2UI v0.9's client-to-server <c>action</c> message. Over AG-UI it travels in
/// the next run's <c>forwardedProps</c> as <c>{ "a2uiAction": { "userAction": { ... } } }</c>, the shape the
/// AG-UI A2UI middleware reads.
/// </summary>
/// <param name="Name">The action's name, from the component's <c>action.event.name</c>.</param>
/// <param name="SurfaceId">The surface the action came from.</param>
/// <param name="SourceComponentId">The component that raised it, such as the submit button.</param>
/// <param name="Timestamp">When the user acted.</param>
/// <param name="Context">The action's context with its bindings resolved: the values the user entered.</param>
public sealed partial record SurfaceUserAction(string Name, string SurfaceId, string SourceComponentId, DateTimeOffset Timestamp, JsonObject Context)
{
    /// <summary>The largest context a client may send, serialized.</summary>
    public const int MaxContextBytes = 16 * 1024;

    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["surfaceId"] = SurfaceId,
        ["sourceComponentId"] = SourceComponentId,
        ["timestamp"] = Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        ["context"] = Context.DeepClone(),
    };

    /// <summary>The run's <c>forwardedProps</c>: <c>{ "a2uiAction": { "userAction": { ... } } }</c>.</summary>
    public JsonElement ToForwardedProperties()
    {
        var forwarded = new JsonObject { [A2ui.ForwardedActionKey] = new JsonObject { [A2ui.UserActionKey] = ToJson() } };
        using var document = JsonDocument.Parse(forwarded.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Reads an action from a run's <c>forwardedProps</c>. A run without one is fine; a malformed one is a
    /// problem the server answers before anything runs. This checks the shape only: whether the surface,
    /// the component and the values are ones the server issued is the server's next question.
    /// </summary>
    public static ForwardedAction Read(JsonElement? forwardedProperties)
    {
        if (forwardedProperties is not { ValueKind: JsonValueKind.Object } forwarded
            || !forwarded.TryGetProperty(A2ui.ForwardedActionKey, out var wrapper))
        {
            return ForwardedAction.None;
        }

        if (JsonNode.Parse(wrapper.GetRawText()) is not JsonObject outer
            || outer.Count != 1
            || outer[A2ui.UserActionKey] is not JsonObject action)
        {
            return ForwardedAction.Invalid("a2uiAction holds exactly one userAction object.");
        }

        var allowed = new[] { "name", "surfaceId", "sourceComponentId", "timestamp", "context" };
        if (action.Select(p => p.Key).FirstOrDefault(k => !allowed.Contains(k)) is { } extra)
        {
            return ForwardedAction.Invalid($"userAction has no '{extra}' property.");
        }

        if (Str(action["name"]) is not { } name || !NamePattern().IsMatch(name)
            || Str(action["surfaceId"]) is not { Length: > 0 and <= 80 } surfaceId
            || Str(action["sourceComponentId"]) is not { Length: > 0 and <= 64 } source)
        {
            return ForwardedAction.Invalid("userAction needs a name, a surfaceId and a sourceComponentId.");
        }

        if (Str(action["timestamp"]) is not { } stamp
            || !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            return ForwardedAction.Invalid("userAction.timestamp is an ISO 8601 date and time.");
        }

        if (action["context"] is not JsonObject context || context.ToJsonString().Length > MaxContextBytes)
        {
            return ForwardedAction.Invalid($"userAction.context is an object of at most {MaxContextBytes} bytes.");
        }

        return ForwardedAction.Valid(new SurfaceUserAction(name, surfaceId, source, timestamp, (JsonObject)context.DeepClone()));
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex NamePattern();
}

/// <summary>What a run's <c>forwardedProps</c> said about a user action.</summary>
public sealed record ForwardedAction(SurfaceUserAction? Action, string? Problem)
{
    public static ForwardedAction None { get; } = new(null, null);

    public bool IsPresent => Action is not null || Problem is not null;

    public static ForwardedAction Valid(SurfaceUserAction action) => new(action, null);

    public static ForwardedAction Invalid(string problem) => new(null, problem);
}
