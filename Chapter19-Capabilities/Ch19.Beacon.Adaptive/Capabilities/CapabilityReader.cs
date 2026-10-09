using System.Text.Json;
using AGUI.Abstractions;

namespace Ch19.Beacon.Adaptive.Capabilities;

/// <summary>
/// The result of reading a capabilities document. <see cref="Capabilities"/> is set only when the
/// document validated; a malformed document is never used, partly or otherwise.
/// </summary>
/// <param name="Capabilities">The declaration, or <see langword="null"/> when it was malformed.</param>
/// <param name="Problems">Why the document was rejected: JSON Pointers to malformed values.</param>
/// <param name="Unrecognized">Members this version of the protocol doesn't define. Kept as notes, never a reason to reject.</param>
public sealed record CapabilityRead(
    AgentCapabilities? Capabilities,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Unrecognized)
{
    public bool IsUsable => Capabilities is not null;
}

/// <summary>
/// Reads an <c>AgentCapabilities</c> document the way the 1.0 specification asks a consumer to:
/// a known field with a value the schema rejects (a string where a boolean belongs, <c>null</c>
/// where an object belongs, a fractional <c>maxIterations</c>) makes the whole declaration unusable,
/// while a member this version doesn't define (a newer agent's addition, or a typo such as the old
/// <c>subAgents</c> key) is noted and skipped, so a newer agent's document doesn't bounce off an older client.
/// </summary>
public static class CapabilityReader
{
    private const long MaxSafeInteger = 9_007_199_254_740_991; // the schema's maximum for integers

    private enum Kind { Boolean, String, Integer, StringArray, Group, Open, Tools, Subagents, NotNull }

    // The 1.0 schema's AgentCapabilities, one entry per object: the members it defines and their kinds.
    private static readonly Dictionary<string, Dictionary<string, Kind>> Shapes = new()
    {
        ["$"] = new()
        {
            ["identity"] = Kind.Group, ["transport"] = Kind.Group, ["tools"] = Kind.Group, ["output"] = Kind.Group,
            ["state"] = Kind.Group, ["multiAgent"] = Kind.Group, ["reasoning"] = Kind.Group,
            ["multimodal"] = Kind.Group, ["execution"] = Kind.Group, ["humanInTheLoop"] = Kind.Group,
            ["custom"] = Kind.Open,
        },
        ["identity"] = new()
        {
            ["name"] = Kind.String, ["type"] = Kind.String, ["description"] = Kind.String, ["version"] = Kind.String,
            ["provider"] = Kind.String, ["documentationUrl"] = Kind.String, ["metadata"] = Kind.Open,
        },
        ["transport"] = Booleans("streaming", "websocket", "httpBinary", "pushNotifications", "resumable"),
        ["tools"] = new() { ["supported"] = Kind.Boolean, ["items"] = Kind.Tools, ["parallelCalls"] = Kind.Boolean, ["clientProvided"] = Kind.Boolean },
        ["output"] = new() { ["structuredOutput"] = Kind.Boolean, ["supportedMimeTypes"] = Kind.StringArray },
        ["state"] = Booleans("snapshots", "deltas", "memory", "persistentState"),
        ["multiAgent"] = new() { ["supported"] = Kind.Boolean, ["delegation"] = Kind.Boolean, ["handoffs"] = Kind.Boolean, ["subagents"] = Kind.Subagents },
        ["reasoning"] = Booleans("supported", "streaming", "encrypted"),
        ["multimodal"] = new() { ["input"] = Kind.Group, ["output"] = Kind.Group },
        ["multimodal/input"] = Booleans("image", "audio", "video", "pdf", "file"),
        ["multimodal/output"] = Booleans("image", "audio"),
        ["execution"] = new() { ["codeExecution"] = Kind.Boolean, ["sandboxed"] = Kind.Boolean, ["maxIterations"] = Kind.Integer, ["maxExecutionTime"] = Kind.Integer },
        ["humanInTheLoop"] = Booleans("supported", "approvals", "interventions", "feedback", "interrupts", "approveWithEdits"),
        ["tool"] = new() { ["name"] = Kind.String, ["description"] = Kind.String, ["parameters"] = Kind.NotNull, ["metadata"] = Kind.Open },
        ["subagent"] = new() { ["name"] = Kind.String, ["description"] = Kind.String },
    };

    private static readonly Dictionary<string, string[]> Required = new()
    {
        ["tool"] = ["name", "description"],
        ["subagent"] = ["name"],
    };

    /// <summary>Reads a capabilities document from UTF-8 JSON.</summary>
    public static CapabilityRead Read(ReadOnlySpan<byte> utf8Json)
    {
        List<string> problems = [];
        List<string> unrecognized = [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException ex)
        {
            return new CapabilityRead(null, [$"(document): not JSON ({ex.Message})"], []);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new CapabilityRead(null, ["(document): not a JSON object"], []);
            }

            Walk(document.RootElement, "$", "", problems, unrecognized);
            if (problems.Count > 0)
            {
                // "A consumer MUST NOT use a declaration it cannot validate": no partial reads.
                return new CapabilityRead(null, problems, unrecognized);
            }

            // Valid by the schema's rules for every member we know; the SDK's source-generated
            // context does the typed read and skips the members we noted as unrecognized.
            var capabilities = document.RootElement.Deserialize(AGUIJsonSerializerContext.Default.AgentCapabilities);
            return new CapabilityRead(capabilities, problems, unrecognized);
        }
    }

    public static CapabilityRead Read(string json) => Read(System.Text.Encoding.UTF8.GetBytes(json));

    private static void Walk(JsonElement obj, string shape, string pointer, List<string> problems, List<string> unrecognized)
    {
        var fields = Shapes[shape];
        foreach (var property in obj.EnumerateObject())
        {
            var at = $"{pointer}/{property.Name}";
            if (!fields.TryGetValue(property.Name, out var kind))
            {
                unrecognized.Add(at);
                continue;
            }

            var value = property.Value;
            var childShape = shape == "$" ? property.Name : $"{shape}/{property.Name}";
            switch (kind)
            {
                case Kind.Boolean when value.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                    problems.Add($"{at}: expected a boolean, found {Describe(value)}");
                    break;
                case Kind.String when value.ValueKind != JsonValueKind.String:
                    problems.Add($"{at}: expected a string, found {Describe(value)}");
                    break;
                case Kind.Integer when !(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n is >= 0 and <= MaxSafeInteger):
                    problems.Add($"{at}: expected an integer from 0 to 2^53-1, found {value.GetRawText()}");
                    break;
                case Kind.NotNull when value.ValueKind == JsonValueKind.Null:
                    problems.Add($"{at}: must not be null");
                    break;
                case Kind.Open when value.ValueKind != JsonValueKind.Object:
                    // Open by key (metadata, custom): anything inside, but the member itself is an object.
                    problems.Add($"{at}: expected an object, found {Describe(value)}");
                    break;
                case Kind.Group when value.ValueKind != JsonValueKind.Object:
                    problems.Add($"{at}: expected an object, found {Describe(value)}");
                    break;
                case Kind.Group:
                    Walk(value, childShape, at, problems, unrecognized);
                    break;
                case Kind.StringArray or Kind.Tools or Kind.Subagents when value.ValueKind != JsonValueKind.Array:
                    problems.Add($"{at}: expected an array, found {Describe(value)}");
                    break;
                case Kind.StringArray:
                    foreach (var (item, index) in value.EnumerateArray().Select((item, index) => (item, index)))
                    {
                        if (item.ValueKind != JsonValueKind.String)
                        {
                            problems.Add($"{at}/{index}: expected a string, found {Describe(item)}");
                        }
                    }

                    break;
                case Kind.Tools or Kind.Subagents:
                    var recordShape = kind == Kind.Tools ? "tool" : "subagent";
                    foreach (var (item, index) in value.EnumerateArray().Select((item, index) => (item, index)))
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            problems.Add($"{at}/{index}: expected an object, found {Describe(item)}");
                            continue;
                        }

                        // A record a field references keeps its own requirements (a SubagentInfo needs its name).
                        foreach (var name in Required[recordShape].Where(name => !item.TryGetProperty(name, out _)))
                        {
                            problems.Add($"{at}/{index}: '{name}' is required");
                        }

                        Walk(item, recordShape, $"{at}/{index}", problems, unrecognized);
                    }

                    break;
            }
        }
    }

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.String => $"the string {value.GetRawText()}",
        JsonValueKind.Number => $"the number {value.GetRawText()}",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array",
        _ => "an object",
    };

    private static Dictionary<string, Kind> Booleans(params string[] names) => names.ToDictionary(n => n, _ => Kind.Boolean);
}
