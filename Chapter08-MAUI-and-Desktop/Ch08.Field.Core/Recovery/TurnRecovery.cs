using System.Collections.Immutable;
using System.Net;
using Beacon.Client.Core;

namespace Ch08.Field.Core.Recovery;

/// <summary>
/// Decides what to tell the user after a turn that didn't finish: the network dropped it, the user
/// stopped it or the app went to the background. It never re-runs anything; it says what is known,
/// what isn't, and which next step is safe. Sending the message again is offered only when the run
/// provably never started; after anything else, the one action offered is a question that only reads.
/// </summary>
public static class TurnRecovery
{
    /// <summary>Assesses the turn that just ended.</summary>
    /// <param name="view">The conversation after the turn ended.</param>
    /// <param name="runsBeforeTurn">How many runs the view held when the turn started; later runs are this turn's.</param>
    /// <param name="safety">Which tools only read, and how to check the others.</param>
    /// <param name="stoppedBy">Who stopped the turn, when it was stopped rather than failed.</param>
    public static RecoveryAdvice Assess(ConversationView view, int runsBeforeTurn, ToolSafety safety, StopCause stoppedBy = StopCause.User)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(safety);

        if (view.Status is not (ConversationStatus.Failed or ConversationStatus.Stopped))
        {
            return RecoveryAdvice.None; // finished, canceled by the agent or waiting for input
        }

        var stopped = view.Status == ConversationStatus.Stopped;
        var lastQuestion = view.Messages.OfType<UserMessageView>().LastOrDefault()?.Text;
        HashSet<string> turnRuns = [.. view.Runs.Skip(runsBeforeTurn).Select(r => r.RunId)];

        // 1. No run started. Only a failure that proves it never did makes sending again safe.
        if (turnRuns.Count == 0)
        {
            if (stopped && stoppedBy == StopCause.User)
            {
                return RecoveryAdvice.None;
            }

            return (stopped ? null : view.Failure) switch
            {
                { Kind: FailureKind.Rejected, StatusCode: not HttpStatusCode.TooManyRequests } failure => new RecoveryAdvice(
                    RecoveryKind.Refused,
                    $"Beacon turned your message down (HTTP {(int?)failure.StatusCode}), so nothing happened. Sending it again won't change that."),
                { RunNeverStarted: true, StatusCode: not null } => SendAgain(
                    "Beacon couldn't start on your message, so nothing happened. Send it again in a moment.", lastQuestion),
                { RunNeverStarted: true } => SendAgain(
                    "Your message didn't reach Beacon, so nothing happened. Send it again when you have a signal.", lastQuestion),
                _ => Check(RecoveryKind.Uncertain,
                    $"{NotStarted(view, stopped)} Beacon may have your message, so check before sending it again.", safety.CheckTarget(lastQuestion), safety),
            };
        }

        // 2. The run started. Did it leave a call with side effects unconfirmed?
        var unconfirmed = view.ToolCalls
            .Where(c => c.RunId is { } runId && turnRuns.Contains(runId))
            .Where(c => c.Status is not (ToolCallStatus.Completed or ToolCallStatus.AwaitingApproval or ToolCallStatus.AwaitingClient))
            .Where(c => !safety.IsReadOnly(c.Name))
            .ToImmutableList();

        var cause = Cause(view, stoppedBy);
        if (!unconfirmed.IsEmpty)
        {
            var call = unconfirmed[0];
            return Check(
                RecoveryKind.CheckFirst,
                $"{cause} while Beacon was {Lower(ToolActivity.Phrases(call).Doing)}. It may have finished on the server, so check before asking again.",
                safety.CheckTarget(call) ?? safety.CheckTarget(lastQuestion),
                safety) with { UnconfirmedCalls = unconfirmed };
        }

        // 3. None was seen, which proves less than it seems: a reset discards whatever the app hadn't read
        //    yet, so the run may have done more than arrived. The person who pressed Stop needs no warning;
        //    anyone else is told to check, and is never offered the same message again.
        return stopped && stoppedBy == StopCause.User
            ? RecoveryAdvice.None
            : Check(
                RecoveryKind.CheckFirst,
                $"{cause} before Beacon finished, and it may have done more than you saw. Check before asking again.",
                safety.CheckTarget(lastQuestion),
                safety);
    }

    private static string NotStarted(ConversationView view, bool stopped) =>
        stopped ? "Beacon Field went to the background before Beacon started answering."
        : view.Failure?.Kind == FailureKind.TimedOut ? "Beacon didn't start answering in time."
        : "The connection failed before Beacon answered.";

    private static string Cause(ConversationView view, StopCause stoppedBy) => view.Status == ConversationStatus.Stopped
        ? stoppedBy == StopCause.Background ? "Beacon Field went to the background" : "You stopped the answer"
        : view.Failure?.Kind switch
        {
            FailureKind.Unreachable or FailureKind.StreamEnded => "The connection dropped",
            FailureKind.TimedOut => "Beacon took too long",
            FailureKind.AgentError => "Beacon reported an error",
            _ => "The answer stopped unexpectedly",
        };

    private static RecoveryAdvice SendAgain(string message, string? question) =>
        new(RecoveryKind.SendAgain, message) { ActionLabel = question is null ? null : "Send again", FollowUp = question };

    // After a run that may have done something, the only action offered is a question that reads.
    private static RecoveryAdvice Check(RecoveryKind kind, string message, string? target, ToolSafety safety) =>
        new(kind, message)
        {
            ActionLabel = target is null ? null : $"Check {target}",
            FollowUp = target is null ? null : safety.CheckQuestion(target),
        };

    private static string Lower(string phrase) =>
        phrase.Length > 0 ? char.ToLowerInvariant(phrase[0]) + phrase[1..] : phrase;
}

/// <summary>Who stopped a turn.</summary>
public enum StopCause
{
    /// <summary>The user pressed Stop.</summary>
    User,

    /// <summary>The app stopped the turn because it stayed in the background too long.</summary>
    Background,
}

/// <summary>What kind of next step is safe after a turn that didn't finish.</summary>
public enum RecoveryKind
{
    /// <summary>Nothing to say: the turn finished, or the user stopped a turn with nothing consequential in flight.</summary>
    None,

    /// <summary>The agent provably never started; sending the message again is safe.</summary>
    SendAgain,

    /// <summary>The agent refused the message before starting; sending it again would be refused too.</summary>
    Refused,

    /// <summary>The run started and may have had side effects, seen or not; check before repeating anything.</summary>
    CheckFirst,

    /// <summary>The client can't tell whether the agent started; check before sending again.</summary>
    Uncertain,
}

/// <summary>What the UI tells the user after a turn that didn't finish, and the one action it offers.</summary>
/// <param name="Kind">The kind of next step that is safe.</param>
/// <param name="Message">What to tell the user.</param>
public sealed record RecoveryAdvice(RecoveryKind Kind, string Message)
{
    /// <summary>No advice.</summary>
    public static RecoveryAdvice None { get; } = new(RecoveryKind.None, string.Empty);

    /// <summary>The label of the offered action, or <see langword="null"/> when none is offered.</summary>
    public string? ActionLabel { get; init; }

    /// <summary>The message the action sends: the question again (only if the run never started), or a read-only check.</summary>
    public string? FollowUp { get; init; }

    /// <summary>Calls with side effects whose outcome the client never heard.</summary>
    public ImmutableList<ToolCallView> UnconfirmedCalls { get; init; } = [];
}
