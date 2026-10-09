using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ch26.Tests.Integration.Infrastructure;

/// <summary>
/// Golden-file tests for event streams. A stream is rendered one event per line, with everything that
/// changes from run to run replaced by a stable placeholder: IDs become <c>&lt;message-1&gt;</c>,
/// <c>&lt;call-1&gt;</c> and so on in order of first appearance, timestamps are removed, the SDK's
/// <c>rawEvent</c> echo collapses to <c>"…"</c>, and a JSON tool result is shown as JSON with its clock
/// values masked. The result is compared with <c>{name}.approved.txt</c> in the calling test's folder.
/// </summary>
/// <remarks>
/// On a mismatch (or a first run) the test writes <c>{name}.received.txt</c> beside the approved file and
/// fails. Read the difference; if the change is intended, rename the received file over the approved one,
/// or run once with <c>BEACON_ACCEPT_SNAPSHOTS=true</c> to accept every received file.
/// </remarks>
public static class StreamSnapshot
{
    private static readonly Dictionary<string, string> IdKinds = new(StringComparer.Ordinal)
    {
        ["threadId"] = "thread",
        ["runId"] = "run",
        ["parentRunId"] = "run",
        ["messageId"] = "message",
        ["parentMessageId"] = "message",
        ["toolCallId"] = "call",
        ["interruptId"] = "interrupt",
    };

    private static readonly HashSet<string> Volatile = new(StringComparer.Ordinal) { "timestamp", "createdAt" };
    private static readonly HashSet<string> Clocks = new(StringComparer.Ordinal) { "placedAt", "chargedAt" };

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The stream as stable text: one line per event, <c>TYPE {fields}</c>.</summary>
    public static string Render(IEnumerable<WireRun> runs)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        var text = new StringBuilder();

        foreach (var run in runs)
        {
            text.Append("# run ").AppendLine(Normalize(JsonNode.Parse(run.Input), ids, counters) is JsonObject input
                ? $"{input["runId"]} (resume: {(input["resume"] is JsonArray resume ? resume.Count : 0)})"
                : "?");

            foreach (var frame in run.Frames)
            {
                var node = Normalize(JsonNode.Parse(frame.GetRawText()), ids, counters)!.AsObject();
                var type = node["type"]!.GetValue<string>();
                node.Remove("type");
                text.Append(type).Append(' ').AppendLine(node.ToJsonString(Compact));
            }
        }

        return text.ToString();
    }

    /// <summary>Compares the rendered stream with the approved file and fails with both paths on a difference.</summary>
    public static void Verify(IEnumerable<WireRun> runs, string name, [CallerFilePath] string sourceFile = "")
    {
        var directory = Path.GetDirectoryName(sourceFile)!; // the folder of the test that calls Verify
        var approved = Path.Combine(directory, $"{name}.approved.txt");
        var received = Path.Combine(directory, $"{name}.received.txt");
        var actual = Render(runs).ReplaceLineEndings("\n");
        var expected = File.Exists(approved) ? File.ReadAllText(approved).ReplaceLineEndings("\n") : null;

        if (actual == expected)
        {
            File.Delete(received);
            return;
        }

        Directory.CreateDirectory(directory);
        if (string.Equals(Environment.GetEnvironmentVariable("BEACON_ACCEPT_SNAPSHOTS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(approved, actual);
            return;
        }

        File.WriteAllText(received, actual);
        Assert.Fail(expected is null
            ? $"No approved snapshot yet. Review {received} and rename it to {Path.GetFileName(approved)}."
            : $"The stream changed. Compare {received} with {approved}.{Environment.NewLine}{FirstDifference(expected, actual)}");
    }

    private static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var left = i < e.Length ? e[i] : "(end)";
            var right = i < a.Length ? a[i] : "(end)";
            if (left != right)
            {
                return $"First difference at line {i + 1}:{Environment.NewLine}- {left}{Environment.NewLine}+ {right}";
            }
        }

        return string.Empty;
    }

    private static JsonNode? Normalize(JsonNode? node, Dictionary<string, string> ids, Dictionary<string, int> counters)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (Volatile.Contains(key))
                    {
                        obj.Remove(key);
                    }
                    else if (key == "rawEvent")
                    {
                        obj[key] = "…";
                    }
                    else if (Clocks.Contains(key))
                    {
                        obj[key] = "<time>";
                    }
                    else if ((IdKinds.TryGetValue(key, out var kind) || (key == "id" && IsInterrupt(obj, out kind)))
                        && value is JsonValue v && v.TryGetValue<string>(out var id))
                    {
                        obj[key] = Placeholder(id, kind!, ids, counters);
                    }
                    else if (key == "content" && value is JsonValue content && content.TryGetValue<string>(out var text) && TryParseObject(text) is { } parsed)
                    {
                        obj[key] = Normalize(parsed, ids, counters); // a JSON tool result, shown as JSON
                    }
                    else
                    {
                        Normalize(value, ids, counters);
                    }
                }

                return obj;

            case JsonArray array:
                foreach (var item in array)
                {
                    Normalize(item, ids, counters);
                }

                return array;

            default:
                return node;
        }
    }

    // Interrupts are the only objects with an "id" that the server mints per run.
    private static bool IsInterrupt(JsonObject obj, out string? kind)
    {
        kind = obj.ContainsKey("reason") || obj.ContainsKey("responseSchema") ? "interrupt" : null;
        return kind is not null;
    }

    private static string Placeholder(string id, string kind, Dictionary<string, string> ids, Dictionary<string, int> counters)
    {
        // A tool result's messageId is its toolCallId, so IDs are shared across kinds: first sighting names it.
        if (!ids.TryGetValue(id, out var placeholder))
        {
            var n = counters[kind] = counters.GetValueOrDefault(kind) + 1;
            ids[id] = placeholder = $"<{kind}-{n}>";
        }

        return placeholder;
    }

    private static JsonNode? TryParseObject(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '['))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
