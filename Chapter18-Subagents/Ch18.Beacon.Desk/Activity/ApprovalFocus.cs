using Microsoft.AspNetCore.Components;

namespace Ch18.Beacon.Desk.Activity;

/// <summary>
/// The keyboard path between the chat and the board, one per circuit. A lane registers the heading of each
/// approval it shows; the chat's "Go to … approval" button moves focus there, and once the answer's turn is
/// over, focus goes back to the chat's composer. Nothing moves focus on its own: lanes change state
/// independently, and focus that jumped whenever one did would pull a keyboard user away mid-sentence.
/// </summary>
public sealed class ApprovalFocus
{
    private readonly Dictionary<string, ElementReference> _headings = new(StringComparer.Ordinal);
    private Func<ValueTask>? _composer;

    /// <summary>A lane shows the approval for <paramref name="interruptId"/> under this heading.</summary>
    public void Register(string interruptId, ElementReference heading) => _headings[interruptId] = heading;

    /// <summary>The approval is gone from the board (answered, or the lane went away).</summary>
    public void Unregister(string interruptId) => _headings.Remove(interruptId);

    /// <summary>Whether a lane is showing this approval, so the chat can offer to go to it.</summary>
    public bool Shows(string interruptId) => _headings.ContainsKey(interruptId);

    /// <summary>The chat says how to focus its composer.</summary>
    public void SetComposer(Func<ValueTask>? focusComposer) => _composer = focusComposer;

    /// <summary>Moves focus to the approval's heading. False when no lane shows it.</summary>
    public async ValueTask<bool> GoToAsync(string interruptId)
    {
        if (!_headings.TryGetValue(interruptId, out var heading))
        {
            return false;
        }

        await heading.FocusAsync();
        return true;
    }

    /// <summary>After an answer: back to where the conversation continues.</summary>
    public ValueTask ReturnToComposerAsync() => _composer?.Invoke() ?? ValueTask.CompletedTask;
}
