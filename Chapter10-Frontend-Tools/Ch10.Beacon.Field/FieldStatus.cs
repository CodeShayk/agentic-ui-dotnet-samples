using Beacon.Client.Core;

namespace Ch10.Beacon.Field;

/// <summary>
/// The status line under Beacon Field's chat, in the technician's words. It depends on nothing from
/// .NET MAUI, so Ch10.Tests compiles this file in as a link and checks it without the workload.
/// </summary>
public static class FieldStatus
{
    public static string Describe(ConversationView view) => view.Status switch
    {
        ConversationStatus.Starting or ConversationStatus.Running => "Beacon is working…",
        ConversationStatus.Stopped => "Stopped.",
        ConversationStatus.Failed => view.Failure?.Kind switch
        {
            FailureKind.Unreachable => "Beacon can't be reached.",
            FailureKind.Rejected => "Beacon refused the request. Start a new chat if it happens again.",
            _ => "Something went wrong. Try again.",
        },
        _ => "Ready",
    };
}
