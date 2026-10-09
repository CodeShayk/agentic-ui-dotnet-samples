using System.Text.Json.Nodes;

namespace Ch20.DeclarativeUi;

/// <summary>
/// The A2UI names Beacon uses, and the conventions that carry A2UI over AG-UI. None of these are in the
/// AG-UI 1.0 specification: the activity type, the operations key and the action key are the ones the AG-UI
/// repository's A2UI middleware and toolkits use (verified 2026-10-03), so a renderer built for that
/// ecosystem understands Beacon's surfaces.
/// </summary>
public static class A2ui
{
    /// <summary>The version stamped on every operation Beacon sends: the v0.9 protocol family.</summary>
    public const string Version = "v0.9";

    /// <summary>The <c>activityType</c> of an <c>ACTIVITY_SNAPSHOT</c> that carries a surface.</summary>
    public const string ActivityType = "a2ui-surface";

    /// <summary>The key, inside the activity's <c>content</c>, of the array of A2UI operations.</summary>
    public const string OperationsKey = "a2ui_operations";

    /// <summary>The key, inside <c>RunAgentInput.forwardedProps</c>, of a user action.</summary>
    public const string ForwardedActionKey = "a2uiAction";

    /// <summary>The key, inside <see cref="ForwardedActionKey"/>, of the action itself.</summary>
    public const string UserActionKey = "userAction";

    /// <summary>The tool a model calls to describe a surface (the name and arguments the AG-UI middleware uses).</summary>
    public const string RenderToolName = "render_a2ui";

    /// <summary>The synthetic tool call through which the agent's model learns about a user action.</summary>
    public const string LogEventToolName = "log_a2ui_event";

    /// <summary>Versions a consumer accepts. A2UI v0.9.1 is a patch of v0.9, and its schemas accept both.</summary>
    public static IReadOnlySet<string> AcceptedVersions { get; } = new HashSet<string>(StringComparer.Ordinal) { "v0.9", "v0.9.1" };

    /// <summary>The activity message ID for the surface a tool call produced.</summary>
    public static string ActivityMessageId(string toolCallId) => $"a2ui-surface-{toolCallId}";

    /// <summary>Reads the operations array out of an activity's content, if it has one.</summary>
    public static JsonArray? OperationsOf(JsonObject? content) => content?[OperationsKey] as JsonArray;
}

/// <summary>Builds A2UI v0.9 operations, each <c>{ "version": "v0.9", "&lt;operation&gt;": { ... } }</c>.</summary>
public static class SurfaceOperations
{
    public static JsonObject CreateSurface(string surfaceId, string catalogId) => new()
    {
        ["version"] = A2ui.Version,
        ["createSurface"] = new JsonObject { ["surfaceId"] = surfaceId, ["catalogId"] = catalogId },
    };

    public static JsonObject UpdateComponents(string surfaceId, JsonArray components) => new()
    {
        ["version"] = A2ui.Version,
        ["updateComponents"] = new JsonObject { ["surfaceId"] = surfaceId, ["components"] = components },
    };

    public static JsonObject UpdateDataModel(string surfaceId, JsonNode? value, string path = "/") => new()
    {
        ["version"] = A2ui.Version,
        ["updateDataModel"] = new JsonObject { ["surfaceId"] = surfaceId, ["path"] = path, ["value"] = value },
    };

    /// <summary>The <c>content</c> of an <c>a2ui-surface</c> activity: <c>{ "a2ui_operations": [ ... ] }</c>.</summary>
    public static JsonObject ActivityContent(JsonArray operations) => new() { [A2ui.OperationsKey] = operations };
}
