using AGUI.Abstractions;
using Beacon.Client.Core;
using Ch15.Forms;

namespace Ch15.Beacon.Field;

/// <summary>
/// Sends Sam's decisions the way the Desk does (Ch15.Beacon.Desk's InterruptAnswers), minus the envelope
/// metadata. Lists rather than collection expressions: the WinUI head's CsWinRT analyzer (CsWinRT1032) rejects
/// collection expressions that target read-only interfaces.
/// </summary>
public static class FieldAnswers
{
    public static Task SendAsync(AgentConversation conversation, QuestionAnswers decided, CancellationToken cancellationToken = default) =>
        decided.Abandoned.Count == 0
            ? conversation.RespondAsync(decided.Answers.Select(a => new InterruptResponseContent(a.InterruptId) { Payload = a.Payload }).ToList(), cancellationToken: cancellationToken)
            : conversation.ContinueAsync(new RunInputs
            {
                Resume =
                [
                    .. decided.Answers.Select(a => new AGUIResume { InterruptId = a.InterruptId, Status = ResumeStatus.Resolved, Payload = a.Payload }),
                    .. decided.Abandoned.Select(id => new AGUIResume { InterruptId = id, Status = ResumeStatus.Cancelled }),
                ],
            }, cancellationToken);
}
