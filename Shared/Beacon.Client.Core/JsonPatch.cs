using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Beacon.Client.Core;

/// <summary>How a JSON Patch application ended.</summary>
public enum JsonPatchResultKind
{
    /// <summary>Every operation applied (operations with an <c>op</c> RFC 6902 doesn't define were skipped with a warning).</summary>
    Applied,

    /// <summary>
    /// The patch is well formed but doesn't apply to this document: a path that doesn't exist, an index
    /// out of range, a failed <c>test</c>. The document keeps its prior value; the run may continue.
    /// </summary>
    NotApplied,

    /// <summary>
    /// The patch isn't a valid JSON Patch: not an array, an operation that isn't an object, or an
    /// operation missing a required member or carrying one of the wrong type (including a path that
    /// isn't a JSON Pointer). AG-UI treats this as a malformed known value, which is fatal to the run.
    /// </summary>
    Malformed,
}

/// <summary>The outcome of applying a JSON Patch.</summary>
/// <param name="Document">The patched document, or the original when the patch failed.</param>
/// <param name="Error">Why the patch didn't apply; <see langword="null"/> when it did.</param>
/// <param name="Warnings">Operations skipped because RFC 6902 doesn't define their <c>op</c>.</param>
public sealed record JsonPatchResult(JsonNode? Document, string? Error, IReadOnlyList<string> Warnings)
{
    private readonly JsonPatchResultKind? _kind;

    /// <summary>
    /// Whether the patch applied, didn't apply or was malformed. When it isn't set explicitly, a result
    /// with an <see cref="Error"/> is <see cref="JsonPatchResultKind.NotApplied"/>.
    /// </summary>
    public JsonPatchResultKind Kind
    {
        get => _kind ?? (Error is null ? JsonPatchResultKind.Applied : JsonPatchResultKind.NotApplied);
        init => _kind = value;
    }

    /// <summary>True when every operation applied.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>
/// Applies RFC 6902 JSON Patch documents to <see cref="JsonNode"/> trees, the way AG-UI's
/// snapshot-delta pattern requires: atomically, without touching the input, and keeping three
/// failures apart. A malformed patch is reported as <see cref="JsonPatchResultKind.Malformed"/>, which
/// the caller treats as fatal; an operation whose <c>op</c> RFC 6902 doesn't define is skipped with a
/// warning; a well-formed patch that doesn't apply (a missing path, a failed <c>test</c>) is
/// <see cref="JsonPatchResultKind.NotApplied"/> and leaves the document as it was.
/// </summary>
/// <remarks>
/// <c>Microsoft.AspNetCore.JsonPatch.SystemTextJson</c> declares a framework reference to the
/// ASP.NET Core shared framework, which a .NET MAUI or Blazor WebAssembly client can't take. This class is small and has
/// no dependencies beyond System.Text.Json.
/// </remarks>
public static class JsonPatch
{
    /// <summary>Applies <paramref name="patch"/> to a copy of <paramref name="document"/>.</summary>
    /// <param name="document">The current document. It is never modified.</param>
    /// <param name="patch">The patch: a JSON array of operations.</param>
    /// <returns>The patched copy, or the original document, an error and the kind of failure.</returns>
    public static JsonPatchResult Apply(JsonNode? document, JsonElement patch)
    {
        // Malformed is a property of the whole patch, so it's judged before anything is applied.
        if (Validate(patch) is { } malformed)
        {
            return new(document, malformed, []) { Kind = JsonPatchResultKind.Malformed };
        }

        // Atomic: work on a copy and publish it only if every operation applies.
        var working = document?.DeepClone();
        List<string> warnings = [];
        var index = 0;

        foreach (var operation in patch.EnumerateArray())
        {
            var error = ApplyOperation(ref working, operation, index, warnings);
            if (error is not null)
            {
                return new(document, $"Operation {index}: {error}", warnings) { Kind = JsonPatchResultKind.NotApplied };
            }

            index++;
        }

        return new(working, null, warnings);
    }

    // The schema's shape rules: an array of objects, each with a string op, and for the six operations
    // RFC 6902 defines, a JSON Pointer path, a JSON Pointer from (move, copy) and a value (add, replace,
    // test). An op RFC 6902 doesn't define is unrecognized, not malformed, so its members aren't judged.
    internal static string? Validate(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Array)
        {
            return "The patch is not a JSON array.";
        }

        var index = 0;
        foreach (var operation in patch.EnumerateArray())
        {
            if (ValidateOperation(operation) is { } error)
            {
                return $"Operation {index}: {error}";
            }

            index++;
        }

        return null;
    }

    private static string? ValidateOperation(JsonElement operation)
    {
        if (operation.ValueKind != JsonValueKind.Object)
        {
            return "it isn't a JSON object.";
        }

        if (!TryGetString(operation, "op", out var op))
        {
            return "it has no 'op', or 'op' isn't a string.";
        }

        if (op is not ("add" or "remove" or "replace" or "move" or "copy" or "test"))
        {
            return null; // unrecognized: skipped with a warning when applied
        }

        if (!TryGetString(operation, "path", out var path) || !TryParsePointer(path, out _))
        {
            return $"'{op}' has a missing or invalid 'path'.";
        }

        if (op is "move" or "copy" && (!TryGetString(operation, "from", out var from) || !TryParsePointer(from, out _)))
        {
            return $"'{op}' has a missing or invalid 'from'.";
        }

        return op is "add" or "replace" or "test" && !operation.TryGetProperty("value", out _)
            ? $"'{op}' has no 'value'."
            : null;
    }

    // Called only for a validated patch, so the members it reads are present and well typed.
    private static string? ApplyOperation(ref JsonNode? document, JsonElement operation, int index, List<string> warnings)
    {
        TryGetString(operation, "op", out var op);
        if (op is not ("add" or "remove" or "replace" or "move" or "copy" or "test"))
        {
            warnings.Add($"operation {index} has op '{op}', which RFC 6902 doesn't define, so it was skipped.");
            return null;
        }

        TryGetString(operation, "path", out var pathText);
        TryParsePointer(pathText, out var path);

        switch (op)
        {
            case "add":
                TryGetValue(operation, out var added);
                return Add(ref document, path, added);

            case "remove":
                return Remove(ref document, path, out _);

            case "replace":
                TryGetValue(operation, out var replacement);
                return Resolve(document, path) is { Found: true } ? Add(ref document, path, replacement, replace: true) : $"'{pathText}' doesn't exist.";

            case "move":
            case "copy":
                TryGetString(operation, "from", out var fromText);
                TryParsePointer(fromText, out var from);

                if (op == "move" && path.Count > from.Count && path.Take(from.Count).SequenceEqual(from))
                {
                    return $"'{fromText}' can't be moved into its own child '{pathText}'.";
                }

                if (Resolve(document, from) is not { Found: true } source)
                {
                    return $"'{fromText}' doesn't exist.";
                }

                if (op == "copy")
                {
                    return Add(ref document, path, source.Value?.DeepClone());
                }

                var removeError = Remove(ref document, from, out var moved);
                return removeError ?? Add(ref document, path, moved);

            default: // "test"
                TryGetValue(operation, out var expected);
                return Resolve(document, path) is { Found: true } actual && JsonNode.DeepEquals(actual.Value, expected)
                    ? null
                    : $"the test at '{pathText}' failed.";
        }
    }

    private static string? Add(ref JsonNode? document, IReadOnlyList<string> path, JsonNode? value, bool replace = false)
    {
        if (path.Count == 0)
        {
            document = value; // the whole document
            return null;
        }

        var parent = Resolve(document, path.Take(path.Count - 1).ToList());
        var name = path[^1];

        switch (parent.Value)
        {
            case JsonObject obj when parent.Found:
                obj[name] = value;
                return null;

            case JsonArray array when parent.Found:
                if (!replace && name == "-")
                {
                    array.Add(value);
                    return null;
                }

                if (!TryParseIndex(name, out var position) || position > array.Count || (replace && position == array.Count))
                {
                    return $"'{name}' isn't a valid index for an array of {array.Count}.";
                }

                if (replace)
                {
                    array[position] = value;
                }
                else
                {
                    array.Insert(position, value);
                }

                return null;

            default:
                return $"the parent of '/{string.Join('/', path)}' doesn't exist or can't hold members.";
        }
    }

    private static string? Remove(ref JsonNode? document, IReadOnlyList<string> path, out JsonNode? removed)
    {
        removed = null;
        if (path.Count == 0)
        {
            return "the document root can't be removed.";
        }

        var parent = Resolve(document, path.Take(path.Count - 1).ToList());
        var name = path[^1];

        switch (parent.Value)
        {
            case JsonObject obj when parent.Found && obj.ContainsKey(name):
                removed = obj[name];
                obj.Remove(name);
                return null;

            case JsonArray array when parent.Found && TryParseIndex(name, out var position) && position < array.Count:
                removed = array[position];
                array.RemoveAt(position);
                return null;

            default:
                return $"'/{string.Join('/', path)}' doesn't exist.";
        }
    }

    // RFC 6901: walks a parsed pointer. Found distinguishes "exists and is null" from "doesn't exist".
    private static (bool Found, JsonNode? Value) Resolve(JsonNode? document, IReadOnlyList<string> path)
    {
        var current = document;
        foreach (var segment in path)
        {
            switch (current)
            {
                case JsonObject obj when obj.TryGetPropertyValue(segment, out var child):
                    current = child;
                    break;

                case JsonArray array when TryParseIndex(segment, out var position) && position < array.Count:
                    current = array[position];
                    break;

                default:
                    return (false, null);
            }
        }

        return (true, current);
    }

    private static bool TryParsePointer(string text, out IReadOnlyList<string> path)
    {
        path = [];
        if (text.Length == 0)
        {
            return true; // the whole document
        }

        if (text[0] != '/')
        {
            return false;
        }

        List<string> segments = [];
        foreach (var raw in text[1..].Split('/'))
        {
            // ~1 is '/', ~0 is '~'; any other escape is malformed.
            for (var i = raw.IndexOf('~'); i >= 0; i = raw.IndexOf('~', i + 1))
            {
                if (i == raw.Length - 1 || raw[i + 1] is not ('0' or '1'))
                {
                    return false;
                }
            }

            segments.Add(raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        }

        path = segments;
        return true;
    }

    // Array indexes are decimal with no leading zeros ("0" is fine, "01" isn't).
    private static bool TryParseIndex(string segment, out int index)
    {
        index = -1;
        return segment.Length > 0
            && (segment == "0" || segment[0] != '0')
            && segment.All(char.IsAsciiDigit)
            && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private static bool TryGetString(JsonElement operation, string name, out string value)
    {
        value = string.Empty;
        if (operation.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }

        return false;
    }

    // "value" may be JSON null, which is a value; only a missing member is an error.
    private static bool TryGetValue(JsonElement operation, out JsonNode? value)
    {
        value = null;
        if (!operation.TryGetProperty("value", out var element))
        {
            return false;
        }

        value = JsonValues.ToNode(element);
        return true;
    }
}
