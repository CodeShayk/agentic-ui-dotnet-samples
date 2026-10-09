using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ch20.DeclarativeUi;

/// <summary>Reading and writing a data model by pointer, with A2UI's upsert semantics.</summary>
internal static class JsonData
{
    public static string? Str(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static bool TryGet(JsonNode? root, DataPointer pointer, out JsonNode? value)
    {
        value = root;
        foreach (var segment in pointer.Segments)
        {
            switch (value)
            {
                case JsonObject obj when obj.TryGetPropertyValue(segment, out var next):
                    value = next;
                    break;
                case JsonArray array when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < array.Count:
                    value = array[index];
                    break;
                default:
                    value = null;
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Sets the value at <paramref name="pointer"/>, creating objects on the way. An array index may name an
    /// existing item or the next one. The root itself can only be replaced by an object.
    /// </summary>
    public static bool TrySet(JsonObject root, DataPointer pointer, JsonNode? value)
    {
        if (pointer.Segments.Count == 0)
        {
            if (value is not JsonObject replacement)
            {
                return false;
            }

            root.Clear();
            foreach (var (name, node) in replacement.ToList())
            {
                replacement.Remove(name);
                root[name] = node;
            }

            return true;
        }

        JsonNode parent = root;
        for (var i = 0; i < pointer.Segments.Count - 1; i++)
        {
            var segment = pointer.Segments[i];
            switch (parent)
            {
                case JsonObject obj:
                    if (obj[segment] is not (JsonObject or JsonArray))
                    {
                        obj[segment] = new JsonObject();
                    }

                    parent = obj[segment]!;
                    break;
                case JsonArray array when Index(segment, array.Count, out var index) && index < array.Count && array[index] is JsonObject or JsonArray:
                    parent = array[index]!;
                    break;
                default:
                    return false;
            }
        }

        var last = pointer.Segments[^1];
        switch (parent)
        {
            case JsonObject obj:
                obj[last] = value;
                return true;
            case JsonArray array when Index(last, array.Count, out var index):
                if (index == array.Count)
                {
                    array.Add(value);
                }
                else
                {
                    array[index] = value;
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>Removes the key at <paramref name="pointer"/> (A2UI: an <c>updateDataModel</c> with no value).</summary>
    public static bool TryRemove(JsonObject root, DataPointer pointer)
    {
        if (pointer.Segments.Count == 0)
        {
            root.Clear();
            return true;
        }

        var parentPointer = pointer with { Segments = [.. pointer.Segments.Take(pointer.Segments.Count - 1)] };
        if (!TryGet(root, parentPointer, out var parent))
        {
            return true; // nothing there to remove
        }

        switch (parent)
        {
            case JsonObject obj:
                obj.Remove(pointer.Segments[^1]);
                return true;
            case JsonArray array when Index(pointer.Segments[^1], array.Count, out var index) && index < array.Count:
                array[index] = null; // A2UI keeps an array's length when an item is removed
                return true;
            default:
                return false;
        }
    }

    private static bool Index(string segment, int count, out int index) =>
        int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index <= count;
}
