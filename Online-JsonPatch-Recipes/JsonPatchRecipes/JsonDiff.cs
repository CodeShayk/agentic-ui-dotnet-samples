using System.Text.Json.Nodes;

namespace JsonPatchRecipes;

/// <summary>
/// Computes a JSON Patch that turns one document into another. The diff is positional: it compares
/// object members by name and array elements by index, so it knows nothing about identity. With
/// <c>guard</c>, every <c>replace</c> and <c>remove</c> is preceded by a <c>test</c> of the value it
/// overwrites, so the patch applies only to a document that still matches <c>before</c> where it changes.
/// </summary>
public static class JsonDiff
{
    public static JsonArray Create(JsonNode? before, JsonNode? after, bool guard = false)
    {
        var patch = new JsonArray();
        Diff(before, after, "", patch, guard);
        return patch;
    }

    private static void Diff(JsonNode? before, JsonNode? after, string path, JsonArray patch, bool guard)
    {
        if (JsonNode.DeepEquals(before, after))
        {
            return;
        }

        switch (before, after)
        {
            case (JsonObject was, JsonObject now):
                foreach (var (name, value) in was)
                {
                    if (!now.ContainsKey(name))
                    {
                        Remove(JsonPointer.Append(path, name), value, patch, guard);
                    }
                }

                foreach (var (name, value) in now)
                {
                    var member = JsonPointer.Append(path, name);
                    if (was.TryGetPropertyValue(name, out var old))
                    {
                        Diff(old, value, member, patch, guard);
                    }
                    else
                    {
                        patch.Add(PatchOps.Add(member, value));
                    }
                }

                break;

            case (JsonArray was, JsonArray now):
                var common = Math.Min(was.Count, now.Count);
                for (var i = 0; i < common; i++)
                {
                    Diff(was[i], now[i], JsonPointer.Append(path, i), patch, guard);
                }

                // Remove surplus elements from the end, so no removal shifts an index a later one uses.
                for (var i = was.Count - 1; i >= now.Count; i--)
                {
                    Remove(JsonPointer.Append(path, i), was[i], patch, guard);
                }

                for (var i = was.Count; i < now.Count; i++)
                {
                    patch.Add(PatchOps.Add(JsonPointer.Append(path, JsonPointer.End), now[i]));
                }

                break;

            default:
                // Different kinds, or different scalars: replace the whole value.
                if (guard)
                {
                    patch.Add(PatchOps.Test(path, before));
                }

                patch.Add(PatchOps.Replace(path, after));
                break;
        }
    }

    private static void Remove(string path, JsonNode? old, JsonArray patch, bool guard)
    {
        if (guard)
        {
            patch.Add(PatchOps.Test(path, old));
        }

        patch.Add(PatchOps.Remove(path));
    }
}
