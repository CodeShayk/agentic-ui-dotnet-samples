using System.Net;
using Beacon.Client.Core;

namespace Ch15.Beacon.Field;

/// <summary>
/// The status line under Beacon Field's chat, in the technician's words. It depends on nothing from
/// .NET MAUI, so Ch15.Tests compiles this file in as a link and checks it without the workload.
/// </summary>
public static class FieldStatus
{
    public static string Describe(ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return view.Status switch
        {
            ConversationStatus.Starting or ConversationStatus.Running => "Beacon is working…",
            ConversationStatus.AwaitingInput => "Beacon is waiting for your answers.",
            ConversationStatus.Stopped => "Stopped.",
            ConversationStatus.Failed => view.OpenInterrupts.FirstOrDefault(i => i.LastAnswerFailure is not null) is { } refused
                ? Unaccepted(refused.LastAnswerFailure!)
                : Failure(view.Failure),
            _ => "Ready",
        };
    }

    // Answers the agent didn't take leave their questions open, with what Sam typed: say so, and the two ways on.
    private static string Unaccepted(RunFailure failure) => failure switch
    {
        { Kind: FailureKind.Rejected, StatusCode: HttpStatusCode.BadRequest } =>
            "Beacon couldn't use those answers. Correct them and send again, or skip the questions.",
        { Kind: FailureKind.Rejected } =>
            "Beacon didn't accept those answers; the questions may have expired at the agent. Send them again, or skip them.",
        _ => "Your answers may not have reached Beacon. Send them again, or skip the questions.",
    };

    private static string Failure(RunFailure? failure) => failure switch
    {
        { Kind: FailureKind.Unreachable } => "Beacon can't be reached. Check that the agent is running.",
        { Kind: FailureKind.Rejected, StatusCode: { } status } => $"Beacon refused the request (HTTP {(int)status}).",
        _ => "Something went wrong. Try again.",
    };
}
