using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;

namespace Beacon.Client.Core;

// Shared state and activity: the two families that follow the snapshot-delta pattern.
// Chapter 12 builds shared state on this slice; Chapter 13 builds activity widgets.
public static partial class ConversationReducer
{
    // STATE_SNAPSHOT replaces the state wholesale, and clears any earlier patch failure: a consumer
    // MUST adopt the next snapshot whatever it skipped before.
    private static ConversationView ReplaceState(ConversationView view, JsonElement snapshot) =>
        view with
        {
            State = view.State with
            {
                Document = JsonValues.ToNode(snapshot),
                HasValue = true,
                Version = view.State.Version + 1,
                LastChangedBy = StateChangeSource.Agent,
                NeedsResync = false,
                LastPatchError = null,
            },
        };

    // STATE_DELTA amends the current state with an RFC 6902 patch. A run with no state starts from {}.
    // A malformed patch fails the run. A well-formed patch that doesn't apply leaves the document
    // exactly as it was and flags the divergence.
    private static ConversationView PatchState(ConversationView view, JsonElement delta)
    {
        var baseline = view.State.HasValue ? view.State.Document : new JsonObject();
        var result = JsonPatch.Apply(baseline, delta);

        if (result.Kind == JsonPatchResultKind.Malformed)
        {
            return Violate(view, $"STATE_DELTA carried a malformed patch: {result.Error}");
        }

        foreach (var warning in result.Warnings)
        {
            view = Warn(view, $"STATE_DELTA: {warning}");
        }

        if (!result.Succeeded)
        {
            return Warn(view, $"A STATE_DELTA didn't apply, so the state kept its prior value: {result.Error}") with
            {
                State = view.State with { NeedsResync = true, LastPatchError = result.Error },
            };
        }

        return view with
        {
            State = view.State with
            {
                Document = result.Document,
                HasValue = true,
                Version = view.State.Version + 1,
                LastChangedBy = StateChangeSource.Agent,
            },
        };
    }

    // The state a run's input carried is the baseline its deltas apply to, or {} when it carried none.
    // With SendState on (the default) that is the view's own state, so nothing changes; a turn that sent
    // other state (RunInputs.State, ConfigureInput) or none at all moves the view to what the agent saw.
    private static ConversationView AdoptSentState(ConversationView view, RunAgentInput input)
    {
        if (input.State is not { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } sent)
        {
            return view.State.HasValue
                ? view with { State = SharedStateView.Empty with { Version = view.State.Version + 1, LastChangedBy = StateChangeSource.Client } }
                : view;
        }

        var document = JsonValues.ToNode(sent);
        if (view.State.HasValue && JsonNode.DeepEquals(view.State.Document, document))
        {
            return view;
        }

        return view with
        {
            State = view.State with
            {
                Document = document,
                HasValue = true,
                Version = view.State.Version + 1,
                LastChangedBy = StateChangeSource.Client,
                NeedsResync = false,
                LastPatchError = null,
            },
        };
    }

    private static ConversationView EditState(ConversationView view, JsonNode? document) =>
        view with
        {
            State = view.State with
            {
                Document = document,
                HasValue = true,
                Version = view.State.Version + 1,
                LastChangedBy = StateChangeSource.Client,
            },
        };

    // ACTIVITY_SNAPSHOT creates an activity message where it arrives, or replaces one in place.
    // replace: false leaves an existing message exactly as it stands.
    private static ConversationView ApplyActivitySnapshot(ConversationView view, ActivitySnapshotEvent e, string? subagentRunId)
    {
        if (JsonValues.ToNode(e.Content) is not JsonObject content)
        {
            // The schema makes content an object, so anything else is a malformed known value.
            return Violate(view, $"ACTIVITY_SNAPSHOT for '{e.MessageId}' had content that isn't an object.");
        }

        var index = IndexOfMessage(view, e.MessageId);
        if (index < 0)
        {
            return view with
            {
                Messages = view.Messages.Add(new ActivityMessageView(e.MessageId, e.ActivityType, content)
                {
                    RunId = view.CurrentRun?.RunId,
                    SubagentRunId = subagentRunId,
                }),
            };
        }

        return view.Messages[index] switch
        {
            ActivityMessageView when e.Replace == false => view,
            ActivityMessageView existing => ReplaceMessage(view, index, existing with
            {
                ActivityType = e.ActivityType,
                Content = content,
                SubagentRunId = subagentRunId, // a replacing snapshot re-mints ownership
            }),
            _ => Warn(view, $"ACTIVITY_SNAPSHOT named '{e.MessageId}', which isn't an activity message, so it was skipped."),
        };
    }

    // ACTIVITY_DELTA patches one activity's content. A malformed patch fails the run, as for state. A
    // delta for a message that doesn't exist is skipped with a warning, never fatal; so is one whose
    // result isn't an object.
    private static ConversationView ApplyActivityDelta(ConversationView view, ActivityDeltaEvent e)
    {
        if (JsonPatch.Validate(e.Patch) is { } malformed)
        {
            return Violate(view, $"ACTIVITY_DELTA for '{e.MessageId}' carried a malformed patch: {malformed}");
        }

        var index = IndexOfMessage(view, e.MessageId);
        if (index < 0 || view.Messages[index] is not ActivityMessageView activity)
        {
            return Warn(view, $"ACTIVITY_DELTA for '{e.MessageId}', which isn't an activity message here, was skipped.");
        }

        var result = JsonPatch.Apply(activity.Content, e.Patch);
        foreach (var warning in result.Warnings)
        {
            view = Warn(view, $"ACTIVITY_DELTA for '{e.MessageId}': {warning}");
        }

        if (!result.Succeeded || result.Document is not JsonObject patched)
        {
            return Warn(view, $"ACTIVITY_DELTA for '{e.MessageId}' didn't apply, so the activity kept its prior content: {result.Error ?? "the result isn't an object"}");
        }

        return ReplaceMessage(view, index, activity with { ActivityType = e.ActivityType, Content = patched });
    }
}
