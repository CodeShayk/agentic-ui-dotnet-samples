namespace Ch10.Beacon.Desk.Screen;

/// <summary>A slot on today's schedule, identified the way the dispatcher sees it: whose and when.</summary>
public sealed record SlotRef(string TechnicianId, DateTimeOffset Start);

/// <summary>The part order the dispatcher is drafting. Beacon may fill it in; only Dana saves it.</summary>
public sealed record PartOrderDraft(string WorkOrderId, string Sku, int Quantity)
{
    /// <summary>Who wrote the current values: the dispatcher, or Beacon on her behalf.</summary>
    public bool FilledByBeacon { get; init; }

    /// <summary>When Dana last saved the draft; <see langword="null"/> while it has unsaved changes.</summary>
    public DateTimeOffset? SavedAt { get; init; }

    public bool IsEmpty => WorkOrderId.Length == 0 && Sku.Length == 0;

    public static PartOrderDraft Empty { get; } = new("", "", 1);
}

/// <summary>What Beacon wants to do, put to the dispatcher before it happens.</summary>
/// <param name="Id">Identifies this request when the dispatcher answers.</param>
/// <param name="Title">One line: what will happen.</param>
/// <param name="Details">The exact values, so the dispatcher confirms what will happen, not a summary of it.</param>
/// <param name="ConfirmLabel">The button that lets it happen.</param>
/// <param name="DeclineLabel">The button that doesn't.</param>
public sealed record ConfirmationRequest(string Id, string Title, IReadOnlyList<string> Details, string ConfirmLabel, string DeclineLabel);

/// <summary>How a confirmation ended.</summary>
public enum ConfirmationAnswer
{
    Confirmed,
    Declined,

    /// <summary>Nobody answered in time. Treated like a decline: nothing happens.</summary>
    NoAnswer,
}

/// <summary>An immutable picture of the dispatcher's screen.</summary>
public sealed record DeskScreenState
{
    public string? OpenWorkOrderId { get; init; }

    public SlotRef? HighlightedSlot { get; init; }

    public PartOrderDraft Draft { get; init; } = PartOrderDraft.Empty;

    public ConfirmationRequest? PendingConfirmation { get; init; }

    /// <summary>The last change Beacon made, in words, for the work area's live region.</summary>
    public string? LastAgentAction { get; init; }
}

/// <summary>
/// Beacon Desk's screen: which work order is open, which schedule slot is highlighted, what the part
/// order draft says and whether Beacon is waiting for the dispatcher to confirm something. One per
/// circuit. Beacon's frontend tools and the dispatcher's own clicks both change it, from any thread;
/// <see cref="Changed"/> tells the components, which marshal to the circuit with <c>InvokeAsync</c>.
/// </summary>
public sealed class DeskScreen(TimeProvider? timeProvider = null)
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private DeskScreenState _state = new();
    private TaskCompletionSource<ConfirmationAnswer>? _pending;

    /// <summary>How long Beacon waits for the dispatcher before treating a confirmation as unanswered.</summary>
    public TimeSpan ConfirmationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Raised after every change, on the thread that made it.</summary>
    public event EventHandler? Changed;

    public DeskScreenState State => Volatile.Read(ref _state);

    public void OpenWorkOrder(string workOrderId, bool byAgent) =>
        Update(s => s with { OpenWorkOrderId = workOrderId, LastAgentAction = byAgent ? $"Beacon opened {workOrderId}." : s.LastAgentAction });

    public void CloseWorkOrder() => Update(s => s with { OpenWorkOrderId = null });

    public void Highlight(SlotRef slot, bool byAgent) =>
        Update(s => s with { HighlightedSlot = slot, LastAgentAction = byAgent ? $"Beacon highlighted {slot.TechnicianId} at {slot.Start:H:mm}." : s.LastAgentAction });

    public void ClearHighlight() => Update(s => s with { HighlightedSlot = null });

    /// <summary>Beacon fills in the draft. Only call this after the dispatcher confirmed it.</summary>
    public void Prefill(PartOrderDraft draft) =>
        Update(s => s with
        {
            Draft = draft with { FilledByBeacon = true, SavedAt = null },
            LastAgentAction = $"Beacon filled in the part order: {draft.Quantity} x {draft.Sku} for {draft.WorkOrderId}.",
        });

    /// <summary>The dispatcher edits the draft herself.</summary>
    public void EditDraft(PartOrderDraft draft) => Update(s => s with { Draft = draft with { FilledByBeacon = false, SavedAt = null } });

    public void SaveDraft() => Update(s => s with { Draft = s.Draft with { SavedAt = _time.GetUtcNow() } });

    public void ClearDraft() => Update(s => s with { Draft = PartOrderDraft.Empty });

    /// <summary>
    /// Puts <paramref name="request"/> to the dispatcher and waits for her answer, for at most
    /// <see cref="ConfirmationTimeout"/>. Cancelling <paramref name="cancellationToken"/> (Stop, or the
    /// circuit closing) withdraws the request and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another confirmation is already waiting.</exception>
    public async Task<ConfirmationAnswer> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var answer = new TaskCompletionSource<ConfirmationAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException("Beacon is already waiting for the dispatcher to confirm something.");
            }

            _pending = answer;
        }

        Update(s => s with { PendingConfirmation = request });
        try
        {
            return await answer.Task.WaitAsync(ConfirmationTimeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return ConfirmationAnswer.NoAnswer;
        }
        finally
        {
            lock (_gate)
            {
                _pending = null;
            }

            Update(s => s with { PendingConfirmation = null }); // withdrawn, whatever happened
        }
    }

    /// <summary>The dispatcher's answer to the pending request. An answer to a request that is no longer pending is ignored.</summary>
    public void Answer(string requestId, bool confirmed)
    {
        TaskCompletionSource<ConfirmationAnswer>? pending;
        lock (_gate)
        {
            pending = State.PendingConfirmation?.Id == requestId ? _pending : null;
        }

        pending?.TrySetResult(confirmed ? ConfirmationAnswer.Confirmed : ConfirmationAnswer.Declined);
    }

    private void Update(Func<DeskScreenState, DeskScreenState> change)
    {
        lock (_gate)
        {
            Volatile.Write(ref _state, change(_state));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
