using System.Text.Json;

namespace JsonPatchRecipes;

/// <summary>What <see cref="PatchShape.Check"/> found.</summary>
/// <param name="Problem">Why the patch is malformed, or <see langword="null"/> when it's well formed.</param>
/// <param name="Unrecognized">Operations whose <c>op</c> RFC 6902 doesn't define: dropped with a warning, not fatal.</param>
public sealed record PatchShapeReport(string? Problem, IReadOnlyList<string> Unrecognized)
{
    public bool IsMalformed => Problem is not null;
}

/// <summary>
/// Tells a malformed patch, which AG-UI makes fatal, from one that is merely unrecognized in part or
/// that won't apply. It checks structure only; whether the paths exist is the applier's question.
/// </summary>
public static class PatchShape
{
    private static readonly HashSet<string> Defined = ["add", "remove", "replace", "move", "copy", "test"];

    public static PatchShapeReport Check(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Array)
        {
            return new("the patch is not a JSON array", []);
        }

        List<string> unrecognized = [];
        var index = 0;
        foreach (var operation in patch.EnumerateArray())
        {
            if (Problem(operation, index, unrecognized) is { } problem)
            {
                return new(problem, unrecognized);
            }

            index++;
        }

        return new(null, unrecognized);
    }

    private static string? Problem(JsonElement operation, int index, List<string> unrecognized)
    {
        if (operation.ValueKind != JsonValueKind.Object)
        {
            return $"operation {index} is not an object";
        }

        if (!operation.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String)
        {
            return $"operation {index} has no 'op' string";
        }

        var name = op.GetString()!;
        if (!Defined.Contains(name))
        {
            // A member of an open union this consumer doesn't know: drop it, warn, keep the rest.
            unrecognized.Add($"operation {index} has op '{name}', which RFC 6902 doesn't define");
            return null;
        }

        if (!IsPointer(operation, "path"))
        {
            return $"operation {index} ('{name}') has no valid 'path'";
        }

        if (name is "add" or "replace" or "test" && !operation.TryGetProperty("value", out _))
        {
            return $"operation {index} ('{name}') has no 'value'";
        }

        if (name is "move" or "copy" && !IsPointer(operation, "from"))
        {
            return $"operation {index} ('{name}') has no valid 'from'";
        }

        return null; // members RFC 6902 doesn't define for this op are ignored, not rejected
    }

    private static bool IsPointer(JsonElement operation, string member) =>
        operation.TryGetProperty(member, out var value)
        && value.ValueKind == JsonValueKind.String
        && JsonPointer.IsValid(value.GetString()!);
}
