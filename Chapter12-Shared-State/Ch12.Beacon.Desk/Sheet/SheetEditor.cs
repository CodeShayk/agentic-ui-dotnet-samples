using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.Client.Core;

namespace Ch12.Beacon.Desk.Sheet;

/// <summary>Where the sheet stands, for the Desk's sync line.</summary>
/// <param name="Version">The conversation's state version: it moves on every change from either side.</param>
/// <param name="LastChangedBy">Who made the latest change.</param>
/// <param name="NeedsResync">A delta from the agent didn't apply here; the agent's next snapshot fixes it.</param>
/// <param name="LastPatchError">Why that delta didn't apply.</param>
/// <param name="HasUnsentEdits">The dispatcher changed the sheet since the agent last received it.</param>
public sealed record SheetSync(int Version, StateChangeSource LastChangedBy, bool NeedsResync, string? LastPatchError, bool HasUnsentEdits);

/// <summary>
/// The dispatcher's side of the shared sheet. Edits apply at once (optimistically) as JSON Patch on
/// the conversation's state; the next run carries them to the agent as <c>RunAgentInput.state</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Conflict policy: the dispatcher's edits win, and nothing is lost silently.</b> An edit made while
/// a run is in progress wasn't in that run's state, so the agent's snapshots don't contain it. The editor
/// keeps such edits pending and applies them again after every change the agent sends. When the agent
/// had changed the same thing, the editor adds a notice; when an edit's target is gone, it says so.
/// </para>
/// <para>
/// The conversation raises <see cref="AgentConversation.Changed"/> on the thread that made the change
/// and doesn't apply the next update until handlers return, so the rebase runs before any later agent
/// change. Edits from the UI thread take the same lock.
/// </para>
/// </remarks>
public sealed class SheetEditor : IDisposable
{
    private readonly AgentConversation _conversation;
    private readonly Lock _gate = new();
    private readonly List<SheetEdit> _pending = [];        // edits the running turn's state didn't carry
    private readonly HashSet<SheetEdit> _reported = [];    // edits a notice was already raised for
    private JsonNode? _sent;                               // the state the running turn started from
    private int _sentVersion;
    private int _rebasedFrom;
    private ImmutableList<string> _notices = [];

    public SheetEditor(AgentConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        _conversation = conversation;
        _conversation.Changed += OnConversationChanged;
    }

    /// <summary>Things the dispatcher should know about edits that met the agent's, newest last.</summary>
    public IReadOnlyList<string> Notices => _notices;

    /// <summary>Where the sheet stands now.</summary>
    public SheetSync Sync
    {
        get
        {
            var state = _conversation.View.State;
            return new SheetSync(state.Version, state.LastChangedBy, state.NeedsResync, state.LastPatchError,
                HasUnsentEdits: state.LastChangedBy == StateChangeSource.Client && state.Version > Volatile.Read(ref _sentVersion));
        }
    }

    /// <summary>Applies the dispatcher's edit to the sheet. Returns false when there was nothing to change.</summary>
    public bool Apply(SheetEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        lock (_gate)
        {
            var view = _conversation.View;
            if (Patch(view.State.Document, edit) is not { } edited)
            {
                return false;
            }

            if (view.IsBusy)
            {
                _pending.Add(edit); // the running turn's state didn't carry it
            }

            _conversation.UpdateState(edited);
            return true;
        }
    }

    /// <summary>A new checklist item ID in the Desk's own namespace, so it can't collide with the agent's (B1, B2, ...).</summary>
    public string NextItemId()
    {
        var document = _conversation.View.State.Document;
        var number = 1;
        while (SheetDocument.IndexOf(document, $"D{number}") >= 0)
        {
            number++;
        }

        return $"D{number}";
    }

    public void DismissNotices() => _notices = [];

    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e)
    {
        lock (_gate)
        {
            switch (e.Action)
            {
                case ConversationReset:
                    (_sent, _sentVersion, _rebasedFrom, _notices) = (null, 0, 0, []);
                    _pending.Clear();
                    _reported.Clear();
                    break;

                case TurnStarted:
                    // Everything edited so far goes out with this turn's first run.
                    _pending.Clear();
                    _reported.Clear();
                    _sent = e.View.State.Document?.DeepClone();
                    Volatile.Write(ref _sentVersion, e.View.State.Version);
                    break;

                case TurnEnded:
                    _pending.Clear(); // what's in the document now goes out with the next turn
                    break;

                case UpdateReceived when e.View.State is { LastChangedBy: StateChangeSource.Agent } state
                                         && state.Version != _rebasedFrom
                                         && _pending.Count > 0:
                    _rebasedFrom = state.Version;
                    Rebase(state.Document);
                    break;
            }
        }
    }

    // Puts the pending edits back on top of the agent's latest change.
    private void Rebase(JsonNode? fromAgent)
    {
        var document = _conversation.View.State.Document; // the latest, which may already hold newer edits
        var changed = false;

        foreach (var edit in _pending)
        {
            var target = edit.PatchFor(document);
            if (target is null)
            {
                Notify(edit, $"Your change to {edit.Subject(_sent)} was dropped: it's no longer on the sheet.");
                continue;
            }

            if (target.Count == 0 || Patch(document, edit) is not { } edited)
            {
                continue; // already in effect
            }

            // The agent's version differs from the one this turn started with, so it changed this too.
            if (!JsonNode.DeepEquals(edit.Target(_sent), edit.Target(fromAgent)))
            {
                Notify(edit, $"Beacon also changed {edit.Subject(_sent)}. Your edit was kept.");
            }

            (document, changed) = (edited, true);
        }

        if (changed)
        {
            _conversation.UpdateState(document);
        }
    }

    private void Notify(SheetEdit edit, string notice)
    {
        if (_reported.Add(edit))
        {
            _notices = _notices.Add(notice);
        }
    }

    // Applies one edit with Beacon.Client.Core's JSON Patch applier, the same one the reducer uses for
    // STATE_DELTA. Null when the edit has nothing to do here.
    private static JsonNode? Patch(JsonNode? document, SheetEdit edit)
    {
        if (edit.PatchFor(document) is not { Count: > 0 } patch)
        {
            return null;
        }

        using var parsed = JsonDocument.Parse(patch.ToJsonString());
        var result = JsonPatch.Apply(document, parsed.RootElement);
        return result.Succeeded ? result.Document : null;
    }

    public void Dispose() => _conversation.Changed -= OnConversationChanged;
}
