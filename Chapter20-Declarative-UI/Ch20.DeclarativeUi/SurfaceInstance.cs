using System.Text.Json;
using System.Text.Json.Nodes;
using static Ch20.DeclarativeUi.JsonData;

namespace Ch20.DeclarativeUi;

/// <summary>Where a surface is in its life on the client.</summary>
public enum SurfaceStatus
{
    /// <summary>The user can fill it in.</summary>
    Open,

    /// <summary>An action is on its way to the agent.</summary>
    Sending,

    /// <summary>An action reached the agent. The surface is read-only from now on.</summary>
    Submitted,
}

/// <summary>
/// One surface on a client: what arrived, the validator's verdict, the live data model and its status. A
/// renderer draws <see cref="Tree"/>, writes input through <see cref="Write"/> and turns a button press into
/// an action with <see cref="CreateAction"/>. Nothing here depends on a UI framework.
/// </summary>
public sealed class SurfaceInstance
{
    private readonly SurfacePolicy _policy;
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);

    private SurfaceInstance(string messageId, JsonObject content, SurfaceVerdict verdict, SurfacePolicy policy)
    {
        MessageId = messageId;
        Content = content;
        Verdict = verdict;
        _policy = policy;
        Data = verdict.Surface is { } surface ? new SurfaceDataModel(surface.Data) : null;
    }

    /// <summary>The activity message that carried the surface.</summary>
    public string MessageId { get; }

    /// <summary>The activity's content, exactly as it arrived.</summary>
    public JsonObject Content { get; }

    public SurfaceVerdict Verdict { get; }

    public SurfaceDocument? Document => Verdict.Surface;

    public SurfaceDataModel? Data { get; }

    public SurfaceStatus Status { get; private set; } = SurfaceStatus.Open;

    public string SurfaceId => Document?.SurfaceId ?? "rejected-surface";

    /// <summary>The surface's first heading, for its accessible name and the transcript.</summary>
    public string Title =>
        Document?.Components.Values.FirstOrDefault(c => c.IsKnown && c.Type == "Text" && Str(c.Properties["variant"]) is "h1" or "h2" or "h3")
            is { } heading && Data is not null
            ? Data.Text(heading.Properties["text"], DataPointer.Root)
            : "A form from Beacon";

    /// <summary>Validates an activity's content and prepares it for rendering.</summary>
    public static SurfaceInstance FromActivity(string messageId, JsonObject content, SurfaceValidator validator)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(validator);
        return new SurfaceInstance(messageId, content, validator.Validate(A2ui.OperationsOf(content)), validator.Policy);
    }

    /// <summary>The tree to draw, rebuilt from the current data so templates follow it.</summary>
    public RenderNode? Tree() =>
        Document is { } document && Data is { } data ? SurfaceTree.Build(document, data, _policy.Catalog, _policy.Limits) : null;

    /// <summary>Writes a user's input through the node's binding for <paramref name="property"/>.</summary>
    public bool Write(RenderNode node, string property, JsonNode? value)
    {
        if (Status != SurfaceStatus.Open || Data is null || !node.IsKnown || !Data.Write(node[property], node.Scope, value))
        {
            return false;
        }

        _touched.Add(node.Key);
        return true;
    }

    /// <summary>Whether the user has changed this node, so its failed checks are worth showing.</summary>
    public bool IsTouched(RenderNode node) => _touched.Contains(node.Key);

    /// <summary>The messages of the node's checks that currently fail.</summary>
    public IReadOnlyList<string> FailedChecks(RenderNode node) =>
        node["checks"] is JsonArray checks && Data is { } data
            ? [.. checks.OfType<JsonObject>().Where(c => !data.Check(c["condition"], node.Scope)).Select(c => Str(c["message"]) ?? "Check this value.")]
            : [];

    /// <summary>
    /// The action a button press sends, or <see langword="null"/> if it can't be sent: the surface isn't open,
    /// the node isn't a button with an event, or one of its checks fails.
    /// </summary>
    public SurfaceUserAction? CreateAction(RenderNode button, DateTimeOffset now)
    {
        if (Status != SurfaceStatus.Open || Data is null || button is not { IsKnown: true, Type: "Button" }
            || button["action"] is not JsonObject action || action["event"] is not JsonObject evt || Str(evt["name"]) is not { } name
            || FailedChecks(button).Count > 0)
        {
            return null;
        }

        return new SurfaceUserAction(name, SurfaceId, button.Component.Id, now, Data.Context(evt["context"] as JsonObject, button.Scope));
    }

    public void MarkSending() => Status = SurfaceStatus.Sending;

    public void MarkSubmitted() => Status = SurfaceStatus.Submitted;

    /// <summary>Back to open, when sending failed and the user should be able to try again.</summary>
    public void MarkOpen()
    {
        if (Status == SurfaceStatus.Sending)
        {
            Status = SurfaceStatus.Open;
        }
    }
}

/// <summary>
/// The surfaces of one conversation, by activity message. A snapshot that replaces a surface's content
/// replaces the instance (and with it the user's unsent edits); the same content keeps it.
/// </summary>
public sealed class SurfaceBoard(SurfaceValidator validator)
{
    private readonly Dictionary<string, SurfaceInstance> _surfaces = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public SurfaceValidator Validator { get; } = validator;

    public IReadOnlyList<SurfaceInstance> Surfaces
    {
        get
        {
            lock (_gate)
            {
                return [.. _surfaces.Values];
            }
        }
    }

    public SurfaceInstance Sync(string messageId, JsonObject content)
    {
        lock (_gate)
        {
            if (_surfaces.TryGetValue(messageId, out var existing)
                && (ReferenceEquals(existing.Content, content) || JsonNode.DeepEquals(existing.Content, content)))
            {
                return existing;
            }

            return _surfaces[messageId] = SurfaceInstance.FromActivity(messageId, content, Validator);
        }
    }

    public SurfaceInstance? Find(string messageId)
    {
        lock (_gate)
        {
            return _surfaces.GetValueOrDefault(messageId);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _surfaces.Clear();
        }
    }
}

/// <summary>Helpers for an activity's content.</summary>
public static class SurfaceActivity
{
    /// <summary>Whether an activity of this type carries an A2UI surface.</summary>
    public static bool IsSurface(string activityType) => string.Equals(activityType, A2ui.ActivityType, StringComparison.Ordinal);

    /// <summary>Parses an activity's content element, as AG-UI types carry it.</summary>
    public static JsonObject? ContentOf(JsonElement content) =>
        content.ValueKind == JsonValueKind.Object ? JsonNode.Parse(content.GetRawText()) as JsonObject : null;
}
