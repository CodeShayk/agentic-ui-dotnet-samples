using System.Text.Json;

namespace Ch03.StreamValidator;

/// <summary>The schema of one event type: its own fields, and whether it can carry a subagentRunId.</summary>
internal sealed record EventShape(string Type, bool Attributable, IReadOnlyList<Field> Fields);

/// <summary>
/// Enforcement: the 31 event types of AG-UI 1.0 and the fields each one carries, after the
/// <c>BaseEvent</c> envelope. Everything here comes from <c>spec/1.0/schema.json</c>.
/// </summary>
internal static class EventShapes
{
    // Every event: the BaseEvent envelope.
    private static readonly Field[] Envelope =
    [
        new("type", FieldKind.String, Required: true),
        new("timestamp", FieldKind.Integer),
        new("rawEvent", FieldKind.NonNull),
        new("metadata", FieldKind.Object),
    ];

    // Events a subagent can produce: the schema's Attributable.
    private static readonly Field[] Attribution = [new("subagentRunId", FieldKind.String)];

    private static Field Required(string name, FieldKind kind = FieldKind.String, FieldCheck? check = null) => new(name, kind, true, check);

    private static Field Optional(string name, FieldKind kind = FieldKind.String, FieldCheck? check = null) => new(name, kind, false, check);

    private static EventShape Run(string type, params Field[] fields) => new(type, Attributable: false, fields);

    private static EventShape Attributed(string type, params Field[] fields) => new(type, Attributable: true, fields);

    public static IReadOnlyDictionary<string, EventShape> ByType { get; } = new[]
    {
        // Runs and steps.
        Run("RUN_STARTED", Required("threadId"), Required("runId"), Optional("protocolVersion"), Optional("parentRunId"),
            Optional("input", FieldKind.Object, ShapeChecker.RunInput)),
        Run("RUN_FINISHED", Required("threadId"), Required("runId"), Optional("result", FieldKind.NonNull),
            Optional("outcome", FieldKind.Object, ShapeChecker.RunOutcome), Optional("usage", FieldKind.Array, ShapeChecker.Usage)),
        Run("RUN_ERROR", Required("message"), Optional("code"), Optional("usage", FieldKind.Array, ShapeChecker.Usage)),
        Attributed("STEP_STARTED", Required("stepName")),
        Attributed("STEP_FINISHED", Required("stepName")),

        // Text messages.
        Attributed("TEXT_MESSAGE_START", Required("messageId"), Optional("role", FieldKind.String, ShapeChecker.TextRole), Optional("name")),
        Attributed("TEXT_MESSAGE_CONTENT", Required("messageId"), Required("delta")),
        Attributed("TEXT_MESSAGE_END", Required("messageId")),
        Attributed("TEXT_MESSAGE_CHUNK", Optional("messageId"), Optional("role", FieldKind.String, ShapeChecker.TextRole), Optional("delta"), Optional("name")),

        // Tool calls.
        Attributed("TOOL_CALL_START", Required("toolCallId"), Required("toolCallName"), Optional("parentMessageId")),
        Attributed("TOOL_CALL_ARGS", Required("toolCallId"), Required("delta")),
        Attributed("TOOL_CALL_END", Required("toolCallId")),
        Attributed("TOOL_CALL_CHUNK", Optional("toolCallId"), Optional("toolCallName"), Optional("parentMessageId"), Optional("delta")),
        Attributed("TOOL_CALL_RESULT", Required("messageId"), Required("toolCallId"), Required("content", FieldKind.Any, ShapeChecker.Content),
            Optional("role", FieldKind.String, ShapeChecker.Constant("tool"))),

        // State.
        Attributed("STATE_SNAPSHOT", Required("snapshot", FieldKind.Any)),
        Attributed("STATE_DELTA", Required("delta", FieldKind.Array, ShapeChecker.JsonPatch)),
        Run("MESSAGES_SNAPSHOT", Required("messages", FieldKind.Array, ShapeChecker.Messages)),

        // Activity.
        Attributed("ACTIVITY_SNAPSHOT", Required("messageId"), Required("activityType"), Required("content", FieldKind.Object), Optional("replace", FieldKind.Boolean)),
        Attributed("ACTIVITY_DELTA", Required("messageId"), Required("activityType"), Required("patch", FieldKind.Array, ShapeChecker.JsonPatch)),

        // Reasoning.
        Attributed("REASONING_START", Required("messageId")),
        Attributed("REASONING_MESSAGE_START", Required("messageId"), Required("role", FieldKind.String, ShapeChecker.Constant("reasoning"))),
        Attributed("REASONING_MESSAGE_CONTENT", Required("messageId"), Required("delta")),
        Attributed("REASONING_MESSAGE_END", Required("messageId")),
        Attributed("REASONING_MESSAGE_CHUNK", Optional("messageId"), Optional("delta")),
        Attributed("REASONING_END", Required("messageId")),
        Attributed("REASONING_ENCRYPTED_VALUE", Required("subtype", FieldKind.String, ShapeChecker.EncryptedSubtype), Required("entityId"),
            Required("encryptedValue")),

        // Subagents. Their subagentRunId names the invocation itself, so they are not Attributable.
        Run("SUBAGENT_STARTED", Required("subagentRunId"), Required("name"), Optional("description"), Optional("parentSubagentRunId"),
            Optional("parentToolCallId"), Optional("parentMessageId")),
        Run("SUBAGENT_FINISHED", Required("subagentRunId"), Optional("result", FieldKind.NonNull), Optional("outcome", FieldKind.Object, ShapeChecker.SubagentOutcome)),
        Run("SUBAGENT_ERROR", Required("subagentRunId"), Required("message"), Optional("code")),

        // Passthrough.
        Attributed("RAW", Required("event", FieldKind.Any), Optional("source")),
        Attributed("CUSTOM", Required("name"), Required("value", FieldKind.Any)),
    }.ToDictionary(shape => shape.Type, StringComparer.Ordinal);

    /// <summary>
    /// Checks one wire event against the schema and returns it ready for the ordering rules, or null
    /// when a consumer would drop it (not an event, an unknown type, or an unknown value in a required
    /// position) or when it lacks the identifiers the ordering rules need.
    /// </summary>
    public static WireEvent? Check(JsonElement json, DiagnosticSink sink)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            sink.Report(Rules.NotAnEvent, $"An event is a JSON object, not {Json.Describe(json)}.");
            return null;
        }

        if (!json.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
        {
            sink.Report(Rules.NotAnEvent, "An event carries its type as a string in 'type'.", "/type");
            return null;
        }

        var type = typeValue.GetString()!;
        sink.EventType = type;

        if (!ByType.TryGetValue(type, out var shape))
        {
            var hint = type.StartsWith("THINKING_", StringComparison.Ordinal)
                ? " THINKING_* was renamed to REASONING_* in 1.0."
                : string.Empty;
            sink.Report(Rules.UnknownEventType, $"'{type}' is not an AG-UI 1.0 event type; a consumer drops the event.{hint}", "/type");
            return null;
        }

        var context = new ShapeContext(sink);
        Field[] fields = [.. Envelope, .. shape.Attributable ? Attribution : [], .. shape.Fields];
        ShapeChecker.CheckObject(context, json, string.Empty, fields, type);

        return context.Dropped ? null : Normalize(sink.EventIndex, type, json, context.Stripped);
    }

    private static WireEvent? Normalize(int index, string type, JsonElement json, IReadOnlySet<string> stripped)
    {
        string? Read(string name) => stripped.Contains(name) ? null : Json.String(json, name);

        var evt = new WireEvent(index, type, json)
        {
            MessageId = Read("messageId"),
            ToolCallId = Read("toolCallId"),
            StepName = Read("stepName"),
            SubagentRunId = type.StartsWith("SUBAGENT_", StringComparison.Ordinal) ? Read("subagentRunId") : null,
            Owner = type.StartsWith("SUBAGENT_", StringComparison.Ordinal) ? null : Read("subagentRunId"),
            Role = Read("role"),
            Name = Read("name"),
            ToolCallName = Read("toolCallName"),
            ParentMessageId = Read("parentMessageId"),
            Delta = Read("delta"),
        };

        // The ordering rules match events by these identifiers. An event missing one has already been
        // reported as malformed; checking its order as well would only repeat the finding.
        var identified = type switch
        {
            "TEXT_MESSAGE_START" or "TEXT_MESSAGE_CONTENT" or "TEXT_MESSAGE_END"
                or "REASONING_START" or "REASONING_END"
                or "REASONING_MESSAGE_START" or "REASONING_MESSAGE_CONTENT" or "REASONING_MESSAGE_END"
                or "ACTIVITY_SNAPSHOT" or "ACTIVITY_DELTA" => evt.MessageId is not null,
            "TOOL_CALL_START" or "TOOL_CALL_ARGS" or "TOOL_CALL_END" => evt.ToolCallId is not null,
            "TOOL_CALL_RESULT" => evt.MessageId is not null && evt.ToolCallId is not null,
            "STEP_STARTED" or "STEP_FINISHED" => evt.StepName is not null,
            "SUBAGENT_STARTED" or "SUBAGENT_FINISHED" or "SUBAGENT_ERROR" => evt.SubagentRunId is not null,
            _ => true,
        };

        return identified ? evt : null;
    }
}
