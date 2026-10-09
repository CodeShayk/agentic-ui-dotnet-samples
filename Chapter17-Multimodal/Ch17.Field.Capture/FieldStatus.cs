using System.Net;
using Beacon.Client.Core;

namespace Ch17.Field.Capture;

/// <summary>Beacon Field's status line, kept out of the MAUI app so the tests can check its words.</summary>
public static class FieldStatus
{
    /// <summary>What the status line says for the conversation's view.</summary>
    public static string Describe(ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return view.Status switch
        {
            ConversationStatus.Starting or ConversationStatus.Running => "Beacon is working…",
            ConversationStatus.Stopped => "Stopped.",
            ConversationStatus.Failed => Failure(view.Failure),
            _ => "Ready",
        };
    }

    // A refused request stays in the conversation and would be refused again, so a refusal points to a new one.
    private static string Failure(RunFailure? failure) => failure switch
    {
        { Kind: FailureKind.Unreachable } => "Beacon can't be reached.",
        { Kind: FailureKind.Rejected, StatusCode: HttpStatusCode.RequestEntityTooLarge } =>
            "The conversation's attachments are too large for Beacon (HTTP 413). Start a new conversation to send more.",
        { Kind: FailureKind.Rejected, StatusCode: { } status } =>
            $"Beacon refused the request (HTTP {(int)status}). Start a new conversation if it happens again.",
        _ => "Something went wrong. Try again.",
    };
}
