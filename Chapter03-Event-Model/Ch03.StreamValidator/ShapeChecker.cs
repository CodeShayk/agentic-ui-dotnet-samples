using System.Text.Json;

namespace Ch03.StreamValidator;

/// <summary>The JSON kind a field must have.</summary>
internal enum FieldKind
{
    String,

    /// <summary>An integer within the JSON safe-integer range (timestamps).</summary>
    Integer,

    /// <summary>A non-negative integer within the safe-integer range (token counts).</summary>
    Count,

    Boolean,
    Object,
    Array,

    /// <summary>Any JSON value, null included (CUSTOM.value, STATE_SNAPSHOT.snapshot, RAW.event).</summary>
    Any,

    /// <summary>Any JSON value except null (rawEvent, RUN_FINISHED.result, forwardedProps).</summary>
    NonNull,
}

internal delegate void FieldCheck(ShapeContext context, JsonElement value, string path);

/// <summary>One field of an object the schema closes.</summary>
internal sealed record Field(string Name, FieldKind Kind, bool Required = false, FieldCheck? Check = null);

/// <summary>The state of enforcement for one event.</summary>
internal sealed class ShapeContext(DiagnosticSink sink)
{
    /// <summary>Top-level properties a consumer would strip (an unrecognized value in an optional position).</summary>
    public HashSet<string> Stripped { get; } = new(StringComparer.Ordinal);

    /// <summary>Set when a consumer would drop the whole event (an unrecognized value in a required position).</summary>
    public bool Dropped { get; set; }

    public void Report(ValidationRule rule, string path, string message) => sink.Report(rule, message, path);

    /// <summary>Records that the value at <paramref name="path"/> is stripped, if it is a top-level property.</summary>
    public void Strip(string path)
    {
        if (path.LastIndexOf('/') == 0)
        {
            Stripped.Add(path[1..]);
        }
    }
}

/// <summary>
/// Structural checks against the 1.0 schema, applied the way the processing model says a consumer
/// applies them: an unknown property or union member is stripped with a warning, a known field with a
/// value the schema rejects is fatal, and an optional field is either present or absent, never null.
/// </summary>
internal static class ShapeChecker
{
    private static readonly Field TypeField = new("type", FieldKind.String, Required: true);

    public static void CheckObject(ShapeContext context, JsonElement obj, string path, IReadOnlyList<Field> fields, string what)
    {
        foreach (var field in fields)
        {
            var fieldPath = Json.Append(path, field.Name);
            if (!obj.TryGetProperty(field.Name, out var value))
            {
                if (field.Required)
                {
                    context.Report(Rules.MissingField, fieldPath, $"{what} requires '{field.Name}'.");
                }

                continue;
            }

            if (value.ValueKind == JsonValueKind.Null && field.Kind != FieldKind.Any)
            {
                if (field.Required)
                {
                    context.Report(Rules.MalformedField, fieldPath, $"'{field.Name}' is required and cannot be null.");
                }
                else
                {
                    context.Report(Rules.NullForAbsent, fieldPath, $"'{field.Name}' is optional: omit it instead of sending null.");
                }

                continue;
            }

            if (!Matches(field.Kind, value))
            {
                context.Report(Rules.MalformedField, fieldPath, $"'{field.Name}' must be {Expected(field.Kind)}, not {Json.Describe(value)}.");
                continue;
            }

            field.Check?.Invoke(context, value, fieldPath);
        }

        foreach (var property in obj.EnumerateObject())
        {
            if (!fields.Any(f => f.Name == property.Name))
            {
                var hint = property.Name == "subagentRunId"
                    ? " Run-scoped events (RUN_*, MESSAGES_SNAPSHOT) carry no attribution."
                    : string.Empty;
                context.Report(
                    Rules.UnknownProperty,
                    Json.Append(path, property.Name),
                    $"'{property.Name}' is not described on {what}; a consumer strips it.{hint}");
            }
        }
    }

    private static bool Matches(FieldKind kind, JsonElement value) => kind switch
    {
        FieldKind.String => value.ValueKind == JsonValueKind.String,
        FieldKind.Integer => Json.IsSafeInteger(value, out _),
        FieldKind.Count => Json.IsSafeInteger(value, out var count) && count >= 0,
        FieldKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        FieldKind.Object => value.ValueKind == JsonValueKind.Object,
        FieldKind.Array => value.ValueKind == JsonValueKind.Array,
        _ => true,
    };

    private static string Expected(FieldKind kind) => kind switch
    {
        FieldKind.String => "a string",
        FieldKind.Integer => "an integer within the JSON safe range",
        FieldKind.Count => "a non-negative integer",
        FieldKind.Boolean => "a boolean",
        FieldKind.Object => "an object",
        FieldKind.Array => "an array",
        _ => "a non-null value",
    };

    // ---- Closed string sets -------------------------------------------------------------------

    private static readonly HashSet<string> TextMessageRoles = ["developer", "system", "assistant", "user"];

    /// <summary>TEXT_MESSAGE_START and _CHUNK: an unknown role is stripped, and the message defaults to assistant.</summary>
    public static void TextRole(ShapeContext context, JsonElement value, string path)
    {
        if (!TextMessageRoles.Contains(value.GetString()!))
        {
            context.Report(Rules.UnknownMember, path,
                $"'{value.GetString()}' is not a text message role; a consumer strips it and the message is an assistant message.");
            context.Strip(path);
        }
    }

    public static FieldCheck Constant(string expected) => (context, value, path) =>
    {
        if (value.GetString() != expected)
        {
            context.Report(Rules.MalformedField, path, $"must be \"{expected}\", not \"{value.GetString()}\".");
        }
    };

    /// <summary>REASONING_ENCRYPTED_VALUE.subtype: an unknown value sits in a required position, so the event is dropped.</summary>
    public static void EncryptedSubtype(ShapeContext context, JsonElement value, string path)
    {
        if (value.GetString() is not ("message" or "tool-call"))
        {
            context.Report(Rules.UnknownMember, path,
                $"'{value.GetString()}' is not a subtype the protocol names; because the field is required, a consumer drops the event.");
            context.Dropped = true;
        }
    }

    public static void StringItems(ShapeContext context, JsonElement array, string path)
    {
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                context.Report(Rules.MalformedField, Json.Append(path, index), $"must be a string, not {Json.Describe(item)}.");
            }

            index++;
        }
    }

    // ---- RUN_FINISHED: outcome, interrupts, usage ---------------------------------------------

    private static readonly Field[] InterruptFields =
    [
        new("id", FieldKind.String, Required: true),
        new("reason", FieldKind.String, Required: true),
        new("message", FieldKind.String),
        new("toolCallId", FieldKind.String),
        new("responseSchema", FieldKind.Object),
        new("expiresAt", FieldKind.String),
        new("metadata", FieldKind.Object),
        new("subagentRunId", FieldKind.String),
    ];

    public static void RunOutcome(ShapeContext context, JsonElement outcome, string path)
    {
        if (!TryReadDiscriminator(context, outcome, path, "the outcome", out var type))
        {
            return;
        }

        switch (type)
        {
            case "success":
                if (outcome.TryGetProperty("interrupts", out _))
                {
                    context.Report(Rules.InterruptsOnOtherOutcome, Json.Append(path, "interrupts"),
                        "A success outcome cannot carry interrupts; a run that stopped to ask ends with the interrupt outcome.");
                }

                CheckObject(context, outcome, path,
                    [TypeField, new("pendingToolCallIds", FieldKind.Array, Check: StringItems), new("interrupts", FieldKind.Any)],
                    "the success outcome");
                break;

            case "interrupt":
                CheckObject(context, outcome, path,
                    [TypeField, new("interrupts", FieldKind.Array, Required: true, Check: Interrupts)],
                    "the interrupt outcome");
                break;

            case "cancelled":
                if (outcome.TryGetProperty("interrupts", out _))
                {
                    context.Report(Rules.InterruptsOnOtherOutcome, Json.Append(path, "interrupts"),
                        "A cancelled run waits for nothing; its outcome carries no interrupts.");
                }

                CheckObject(context, outcome, path, [TypeField, new("interrupts", FieldKind.Any)], "the cancelled outcome");
                break;

            default:
                context.Report(Rules.UnknownMember, Json.Append(path, "type"),
                    $"'{type}' is not a RUN_FINISHED outcome; a consumer strips the outcome, so the run reads as a success.");
                context.Strip(path);
                break;
        }
    }

    private static void Interrupts(ShapeContext context, JsonElement interrupts, string path)
    {
        if (interrupts.GetArrayLength() == 0)
        {
            context.Report(Rules.MalformedField, path, "An interrupt outcome carries at least one interrupt.");
        }

        EachObject(context, interrupts, path, "an interrupt",
            (item, itemPath) => CheckObject(context, item, itemPath, InterruptFields, "an interrupt"));
    }

    private static readonly Field[] TokenUsageFields =
    [
        new("provider", FieldKind.String),
        new("model", FieldKind.String),
        new("inputTokens", FieldKind.Count),
        new("outputTokens", FieldKind.Count),
        new("totalTokens", FieldKind.Count),
        new("reasoningTokens", FieldKind.Count),
        new("cachedInputTokens", FieldKind.Count),
        new("cacheWriteInputTokens", FieldKind.Count),
    ];

    /// <summary>RUN_FINISHED and RUN_ERROR usage: one entry per provider and model, under one accounting.</summary>
    public static void Usage(ShapeContext context, JsonElement usage, string path) =>
        EachObject(context, usage, path, "a TokenUsage entry", (entry, entryPath) =>
        {
            CheckObject(context, entry, entryPath, TokenUsageFields, "a TokenUsage entry");

            var input = Count(entry, "inputTokens");
            var output = Count(entry, "outputTokens");
            var total = Count(entry, "totalTokens");
            if (input is { } i && output is { } o && total is { } t && t != i + o)
            {
                context.Report(Rules.TotalMismatch, Json.Append(entryPath, "totalTokens"),
                    $"totalTokens is {t}, but inputTokens + outputTokens is {i + o}.");
            }

            var cached = (Count(entry, "cachedInputTokens") ?? 0) + (Count(entry, "cacheWriteInputTokens") ?? 0);
            if (input is { } totalInput && cached > totalInput)
            {
                context.Report(Rules.PartExceedsTotal, entryPath,
                    $"Cache reads and writes ({cached}) are parts of inputTokens ({totalInput}), never additions to it.");
            }

            if (output is { } totalOutput && Count(entry, "reasoningTokens") is { } reasoning && reasoning > totalOutput)
            {
                context.Report(Rules.PartExceedsTotal, entryPath,
                    $"reasoningTokens ({reasoning}) is part of outputTokens ({totalOutput}), never an addition to it.");
            }
        });

    private static long? Count(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && Json.IsSafeInteger(value, out var count) && count >= 0 ? count : null;

    // ---- SUBAGENT_FINISHED outcome ------------------------------------------------------------

    public static void SubagentOutcome(ShapeContext context, JsonElement outcome, string path)
    {
        if (!TryReadDiscriminator(context, outcome, path, "the outcome", out var type))
        {
            return;
        }

        switch (type)
        {
            case "success":
                if (outcome.TryGetProperty("interruptIds", out _))
                {
                    context.Report(Rules.SuccessWithInterruptIds, Json.Append(path, "interruptIds"),
                        "A subagent that succeeded is waiting for nothing; only the suspended outcome carries interrupt ids.");
                }

                CheckObject(context, outcome, path, [TypeField, new("interruptIds", FieldKind.Any)], "the success outcome");
                break;

            case "suspended":
                CheckObject(context, outcome, path,
                    [TypeField, new("interruptIds", FieldKind.Array, Check: StringItems)],
                    "the suspended outcome");
                break;

            default:
                context.Report(Rules.UnknownMember, Json.Append(path, "type"),
                    $"'{type}' is not a SUBAGENT_FINISHED outcome; a consumer strips it, so the invocation reads as a success.");
                context.Strip(path);
                break;
        }
    }

    // ---- JSON Patch (STATE_DELTA, ACTIVITY_DELTA) ---------------------------------------------

    /// <summary>
    /// RFC 6902 operations. Operations are open objects, so extra members are legal; an unknown
    /// <c>op</c> is an unrecognized union member that a consumer drops from the patch.
    /// </summary>
    public static void JsonPatch(ShapeContext context, JsonElement patch, string path) =>
        EachObject(context, patch, path, "a patch operation", (operation, operationPath) =>
        {
            if (!TryReadDiscriminator(context, operation, operationPath, "a patch operation", out var op, "op"))
            {
                return;
            }

            string[] required = op switch
            {
                "add" or "replace" or "test" => ["path", "value"],
                "remove" => ["path"],
                "move" or "copy" => ["from", "path"],
                _ => [],
            };

            if (required.Length == 0)
            {
                context.Report(Rules.UnknownMember, Json.Append(operationPath, "op"),
                    $"'{op}' is not an RFC 6902 operation; a consumer drops it from the patch and applies the rest.");
                return;
            }

            foreach (var member in required)
            {
                var memberPath = Json.Append(operationPath, member);
                if (!operation.TryGetProperty(member, out var value))
                {
                    context.Report(Rules.MissingField, memberPath, $"A '{op}' operation requires '{member}'.");
                }
                else if (member is "path" or "from" && (value.ValueKind != JsonValueKind.String || !Json.IsJsonPointer(value.GetString()!)))
                {
                    context.Report(Rules.MalformedField, memberPath, $"'{member}' must be a JSON Pointer such as \"/items/0\".");
                }
            }
        });

    // ---- Messages and content parts (MESSAGES_SNAPSHOT, RUN_STARTED.input) -----------------------

    private static readonly Field[] ToolCallFields =
    [
        new("id", FieldKind.String, Required: true),
        new("type", FieldKind.String, Required: true, Check: Constant("function")),
        new("function", FieldKind.Object, Required: true, Check: (context, function, path) => CheckObject(context, function, path,
            [new("name", FieldKind.String, Required: true), new("arguments", FieldKind.String, Required: true)], "a function call")),
        new("encryptedValue", FieldKind.String),
        new("metadata", FieldKind.Object),
    ];

    public static void Messages(ShapeContext context, JsonElement messages, string path) =>
        EachObject(context, messages, path, "a message", (message, messagePath) => Message(context, message, messagePath));

    private static void Message(ShapeContext context, JsonElement message, string path)
    {
        if (!TryReadDiscriminator(context, message, path, "a message", out var role, "role"))
        {
            return;
        }

        Field id = new("id", FieldKind.String, Required: true);
        Field roleField = new("role", FieldKind.String, Required: true);
        Field metadata = new("metadata", FieldKind.Object);
        Field attribution = new("subagentRunId", FieldKind.String);
        Field encryptedValue = new("encryptedValue", FieldKind.String);
        Field name = new("name", FieldKind.String);
        Field[] common = [id, roleField, metadata, attribution];

        Field[]? fields = role switch
        {
            "developer" or "system" => [.. common, name, encryptedValue, new("content", FieldKind.String, Required: true)],
            "user" => [.. common, name, encryptedValue, new("content", FieldKind.Any, Required: true, Check: Content)],
            "assistant" => [.. common, name, encryptedValue, new("content", FieldKind.String),
                new("toolCalls", FieldKind.Array, Check: (c, calls, p) =>
                    EachObject(c, calls, p, "a tool call", (call, callPath) => CheckObject(c, call, callPath, ToolCallFields, "a tool call")))],
            "tool" => [.. common, encryptedValue, new("content", FieldKind.Any, Required: true, Check: Content),
                new("toolCallId", FieldKind.String, Required: true), new("error", FieldKind.String)],
            "activity" => [.. common, new("activityType", FieldKind.String, Required: true), new("content", FieldKind.Object, Required: true)],
            "reasoning" => [.. common, encryptedValue, new("content", FieldKind.String, Required: true)],
            _ => null,
        };

        if (fields is null)
        {
            context.Report(Rules.UnknownMember, path,
                $"'{role}' is not a message role; a consumer drops this message from the list and keeps the rest.");
            return;
        }

        CheckObject(context, message, path, fields, $"a {role} message");
    }

    /// <summary>User and tool message content, and TOOL_CALL_RESULT content: a string or a list of parts.</summary>
    public static void Content(ShapeContext context, JsonElement content, string path)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return;
            case JsonValueKind.Array:
                EachObject(context, content, path, "a content part", (part, partPath) => ContentPart(context, part, partPath));
                return;
            default:
                context.Report(Rules.MalformedField, path, $"must be a string or a list of content parts, not {Json.Describe(content)}.");
                return;
        }
    }

    private static void ContentPart(ShapeContext context, JsonElement part, string path)
    {
        if (!TryReadDiscriminator(context, part, path, "a content part", out var type))
        {
            return;
        }

        Field[] media =
        [
            TypeField,
            new("id", FieldKind.String),
            new("source", FieldKind.Object, Required: true, Check: PartSource),
            new("metadata", FieldKind.NonNull),
        ];

        switch (type)
        {
            case "text":
                CheckObject(context, part, path,
                    [TypeField, new("id", FieldKind.String), new("text", FieldKind.String, Required: true), new("metadata", FieldKind.NonNull)],
                    "a text part");
                break;
            case "image" or "audio" or "video" or "document":
                CheckObject(context, part, path, media, $"an {type} part");
                break;
            default:
                var hint = type == "binary" ? " The pre-1.0 binary part became the image, audio, video and document parts." : string.Empty;
                context.Report(Rules.UnknownMember, path,
                    $"'{type}' is not a content part type; a consumer drops this part and keeps the rest of the list.{hint}");
                break;
        }
    }

    private static void PartSource(ShapeContext context, JsonElement source, string path)
    {
        if (!TryReadDiscriminator(context, source, path, "a part source", out var type))
        {
            return;
        }

        Field value = new("value", FieldKind.String, Required: true);
        Field mimeType = new("mimeType", FieldKind.String);
        switch (type)
        {
            case "data":
                CheckObject(context, source, path, [TypeField, value, new("mimeType", FieldKind.String, Required: true)], "a data source");
                break;
            case "url":
                CheckObject(context, source, path, [TypeField, value, mimeType], "a url source");
                break;
            case "file":
                CheckObject(context, source, path, [TypeField, value, new("provider", FieldKind.String), mimeType], "a file source");
                break;
            default:
                context.Report(Rules.UnknownMember, Json.Append(path, "type"),
                    $"'{type}' is not a source type; a consumer removes the whole part, since a part cannot lose its source.");
                break;
        }
    }

    // ---- RUN_STARTED.input --------------------------------------------------------------------

    public static void RunInput(ShapeContext context, JsonElement input, string path) =>
        CheckObject(context, input, path,
        [
            new("threadId", FieldKind.String, Required: true),
            new("runId", FieldKind.String, Required: true),
            new("protocolVersion", FieldKind.String),
            new("parentRunId", FieldKind.String),
            new("state", FieldKind.NonNull),
            new("messages", FieldKind.Array, Required: true, Check: Messages),
            new("tools", FieldKind.Array, Check: (c, tools, p) => EachObject(c, tools, p, "a tool", (tool, toolPath) =>
                CheckObject(c, tool, toolPath,
                [
                    new("name", FieldKind.String, Required: true),
                    new("description", FieldKind.String, Required: true),
                    new("parameters", FieldKind.NonNull),
                    new("metadata", FieldKind.Object),
                ], "a tool"))),
            new("context", FieldKind.Array, Check: (c, items, p) => EachObject(c, items, p, "a context entry", (item, itemPath) =>
                CheckObject(c, item, itemPath,
                    [new("description", FieldKind.String, Required: true), new("value", FieldKind.String, Required: true)],
                    "a context entry"))),
            new("forwardedProps", FieldKind.NonNull),
            new("resume", FieldKind.Array, Check: (c, entries, p) => EachObject(c, entries, p, "a resume entry", (entry, entryPath) =>
                CheckObject(c, entry, entryPath,
                [
                    new("interruptId", FieldKind.String, Required: true),
                    new("status", FieldKind.String, Required: true, Check: (c2, status, statusPath) =>
                    {
                        if (status.GetString() is not ("resolved" or "cancelled"))
                        {
                            c2.Report(Rules.UnknownMember, statusPath, $"'{status.GetString()}' is not a resume status.");
                        }
                    }),
                    new("payload", FieldKind.NonNull),
                    new("metadata", FieldKind.Object),
                ], "a resume entry"))),
        ], "the run input");

    // ---- Helpers ------------------------------------------------------------------------------

    private static void EachObject(ShapeContext context, JsonElement array, string path, string what, Action<JsonElement, string> check)
    {
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var itemPath = Json.Append(path, index++);
            if (item.ValueKind != JsonValueKind.Object)
            {
                context.Report(Rules.MalformedField, itemPath, $"{what} must be an object, not {Json.Describe(item)}.");
                continue;
            }

            check(item, itemPath);
        }
    }

    private static bool TryReadDiscriminator(
        ShapeContext context, JsonElement obj, string path, string what, out string value, string name = "type")
    {
        value = string.Empty;
        if (!obj.TryGetProperty(name, out var discriminator))
        {
            context.Report(Rules.MissingField, Json.Append(path, name), $"{what} requires '{name}'.");
            return false;
        }

        if (discriminator.ValueKind != JsonValueKind.String)
        {
            context.Report(Rules.MalformedField, Json.Append(path, name), $"'{name}' must be a string, not {Json.Describe(discriminator)}.");
            return false;
        }

        value = discriminator.GetString()!;
        return true;
    }
}
