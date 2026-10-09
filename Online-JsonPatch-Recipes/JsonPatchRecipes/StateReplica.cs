using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.Client.Core;

namespace JsonPatchRecipes;

/// <summary>What a state event did to a <see cref="StateReplica"/>.</summary>
public enum StateOutcome
{
    /// <summary>The event wasn't a state event.</summary>
    None,

    /// <summary>A snapshot replaced the state.</summary>
    Replaced,

    /// <summary>A delta applied in full.</summary>
    Patched,

    /// <summary>A well-formed delta didn't apply; the state kept its prior value and needs a snapshot.</summary>
    Skipped,

    /// <summary>The delta was malformed. AG-UI makes that fatal: treat the run as failed.</summary>
    Malformed,
}

/// <summary>
/// A client's copy of the shared state, for code that applies <c>STATE_*</c> events itself (a console
/// tool, a test harness, a view model) rather than through <see cref="AgentConversation"/>.
/// </summary>
public sealed class StateReplica
{
    /// <summary>The current state. Replaced, never mutated, so a caller may keep a reference.</summary>
    public JsonNode? Document { get; private set; } = new JsonObject();

    /// <summary>True after a delta was skipped, until the next snapshot.</summary>
    public bool NeedsResync { get; private set; }

    public List<string> Warnings { get; } = [];

    /// <summary>A run starts from the state its input carried, or from {} when it carried none.</summary>
    public void BeginRun(JsonElement? inputState) =>
        Document = inputState is { } state ? JsonNode.Parse(state.GetRawText()) : new JsonObject();

    public StateOutcome Apply(BaseEvent evt) => evt switch
    {
        StateSnapshotEvent snapshot => Replace(snapshot.Snapshot),
        StateDeltaEvent delta => Patch(delta.Delta),
        _ => StateOutcome.None,
    };

    private StateOutcome Replace(JsonElement snapshot)
    {
        // Whatever the replica skipped before is spent: a snapshot is the new baseline.
        Document = JsonNode.Parse(snapshot.GetRawText());
        NeedsResync = false;
        return StateOutcome.Replaced;
    }

    private StateOutcome Patch(JsonElement delta)
    {
        if (PatchShape.Check(delta).Problem is { } problem)
        {
            Warnings.Add($"STATE_DELTA is malformed: {problem}.");
            return StateOutcome.Malformed;
        }

        var result = JsonPatch.Apply(Document, delta); // atomic: a copy is patched, the original is untouched
        Warnings.AddRange(result.Warnings);
        if (!result.Succeeded)
        {
            Warnings.Add($"STATE_DELTA didn't apply, so the state kept its prior value: {result.Error}");
            NeedsResync = true;
            return StateOutcome.Skipped;
        }

        Document = result.Document;
        return StateOutcome.Patched;
    }
}
