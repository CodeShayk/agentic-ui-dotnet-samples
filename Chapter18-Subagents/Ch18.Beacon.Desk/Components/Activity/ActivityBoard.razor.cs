using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch18.Beacon.Desk.Activity;
using Microsoft.AspNetCore.Components;

namespace Ch18.Beacon.Desk.Components.Activity;

/// <summary>
/// Renders the circuit's conversation as a <see cref="SpecialistBoard"/>, and sends the dispatcher's answer
/// when a lane waits on an approval.
/// </summary>
public sealed partial class ActivityBoard : IDisposable
{
    private readonly Dictionary<string, LaneStatus> _heard = new(StringComparer.Ordinal);
    private RenderThrottle _throttle = default!;
    private ConversationView? _projectedFrom;
    private SpecialistBoard _board = SpecialistBoard.Empty;
    private string? _announcement;

    [Inject] private AgentConversation Conversation { get; set; } = default!;

    [Inject] private ILogger<ActivityBoard> Logger { get; set; } = default!;

    [Inject] private ApprovalFocus Focus { get; set; } = default!;

    /// <summary>The shortest time between two renders while a run streams.</summary>
    [Parameter] public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    protected override void OnInitialized()
    {
        _throttle = new RenderThrottle(() => InvokeAsync(StateHasChanged), RenderInterval);
        Conversation.Changed += OnConversationChanged;
    }

    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e) => _ = _throttle.RequestAsync();

    private SpecialistBoard Project(ConversationView view)
    {
        if (!ReferenceEquals(view, _projectedFrom))
        {
            (_board, _projectedFrom) = (SpecialistBoard.From(view), view);
            Announce(_board);
        }

        return _board;
    }

    // One sentence per lane that reached a state a person should hear about, once.
    private void Announce(SpecialistBoard board)
    {
        List<string> sentences = [];
        foreach (var lane in board.Runs.SelectMany(r => Flatten(r.Lanes)))
        {
            if (_heard.TryGetValue(lane.SubagentRunId, out var before) && before == lane.Status)
            {
                continue;
            }

            _heard[lane.SubagentRunId] = lane.Status;
            var sentence = lane.Status switch
            {
                LaneStatus.Done => $"{lane.Label} finished.",
                LaneStatus.Failed => $"{lane.Label} failed: {lane.Error}",
                LaneStatus.WaitingForApproval => $"{lane.Label} is waiting for your approval.",
                _ => null,
            };

            if (sentence is not null)
            {
                sentences.Add(sentence);
            }
        }

        if (sentences.Count > 0)
        {
            _announcement = string.Join(" ", sentences);
        }
    }

    private static IEnumerable<SpecialistLane> Flatten(IEnumerable<SpecialistLane> lanes) =>
        lanes.SelectMany(l => Flatten(l.Children).Prepend(l));

    private static string RunTitle(ActivityRun run, bool latest)
    {
        var count = run.Lanes.Count == 1 ? "1 specialist" : $"{run.Lanes.Count} specialists";
        return latest ? $"This request · {count}" : $"Earlier request · {count}";
    }

    private Task ApproveAsync(InterruptView interrupt) => AnswerAsync(interrupt, LaneApprovals.ApproveAsync);

    private Task DeclineAsync(InterruptView interrupt) => AnswerAsync(interrupt, LaneApprovals.DeclineAsync);

    private async Task AnswerAsync(InterruptView interrupt, Func<AgentConversation, InterruptView, CancellationToken, Task> answer)
    {
        if (Conversation.View.IsBusy)
        {
            return;
        }

        try
        {
            await answer(Conversation, interrupt, CancellationToken.None);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Refused before sending (expired, or a turn already running). Unhandled, it would end the circuit.
            Logger.LogWarning(ex, "The answer to {InterruptId} wasn't sent.", interrupt.Id);
        }

        await _throttle.FlushAsync();

        // The card that had focus is gone once the answer is accepted; the conversation continues in the chat.
        if (!Conversation.View.OpenInterrupts.Any())
        {
            await Focus.ReturnToComposerAsync();
        }
    }

    public void Dispose()
    {
        Conversation.Changed -= OnConversationChanged;
        _throttle.Dispose();
    }
}
