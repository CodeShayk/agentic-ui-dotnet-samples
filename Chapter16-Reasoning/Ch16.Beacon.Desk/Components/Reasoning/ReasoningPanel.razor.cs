using Ch16.Beacon.Desk.Components.Chat;
using Microsoft.AspNetCore.Components;

namespace Ch16.Beacon.Desk.Components.Reasoning;

/// <summary>
/// The collapsible "How I got here" panel for one reply. Its open state belongs to the caller
/// (<c>@bind-Expanded</c>), because the reply moves from the streaming slot to the list when it finishes,
/// and a panel Dana opened mid-stream must stay open.
/// </summary>
public sealed partial class ReasoningPanel
{
    private readonly string _bodyId = $"ch16-reasoning-{Guid.NewGuid():N}";

    /// <summary>The reply's reasoning and tool calls, in order.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<ReplyStep> Steps { get; set; } = [];

    /// <summary>Whether the steps are showing. Collapsed by default.</summary>
    [Parameter] public bool Expanded { get; set; }

    [Parameter] public EventCallback<bool> ExpandedChanged { get; set; }

    [Parameter] public string Label { get; set; } = "How I got here";

    // The first level of disclosure: what Beacon is doing now, or what it did, in a few words.
    private string Status
    {
        get
        {
            if (Steps.Any(s => s is { Kind: ReplyStepKind.Thought, InProgress: true }))
            {
                return "Thinking…";
            }

            if (Steps.FirstOrDefault(s => s is { Kind: ReplyStepKind.Lookup, InProgress: true }) is { } lookup)
            {
                return lookup.Text + "…";
            }

            var thoughts = Steps.Count(s => s.Kind == ReplyStepKind.Thought);
            var lookups = Steps.Count(s => s.Kind == ReplyStepKind.Lookup);
            List<string> parts = [];
            if (thoughts > 0)
            {
                parts.Add(thoughts == 1 ? "1 thought" : $"{thoughts} thoughts");
            }

            if (lookups > 0)
            {
                parts.Add(lookups == 1 ? "1 lookup" : $"{lookups} lookups");
            }

            return string.Join(" · ", parts);
        }
    }

    private Task ToggleAsync() => ExpandedChanged.InvokeAsync(!Expanded);
}
