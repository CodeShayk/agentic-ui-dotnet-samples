using Beacon.Client.Core;
using Beacon.Desk.Ui;

namespace Ch26.Beacon.Desk.Workspace;

/// <summary>What the chat panel shows, projected from the conversation's view state.</summary>
public sealed record DeskTranscript(IReadOnlyList<TranscriptMessage> Messages, TranscriptMessage? Streaming)
{
    public static DeskTranscript From(ConversationView view)
    {
        List<TranscriptMessage> messages = [];
        TranscriptMessage? streaming = null;

        foreach (var message in view.Messages)
        {
            switch (message)
            {
                case UserMessageView user:
                    messages.Add(new TranscriptMessage(user.Id, ChatAuthor.User, user.Text));
                    break;

                case AssistantMessageView { Text.Length: > 0, IsStreaming: true } live:
                    if (streaming is not null)
                    {
                        messages.Add(streaming);
                    }

                    streaming = new TranscriptMessage(live.Id, ChatAuthor.Assistant, live.Text);
                    break;

                case AssistantMessageView { Text.Length: > 0 } done:
                    messages.Add(new TranscriptMessage(done.Id, ChatAuthor.Assistant, done.Text) { IsPartial = done.IsPartial });
                    break;
            }
        }

        return new DeskTranscript(messages, streaming);
    }

    public static ChatStatus StatusOf(ConversationView view) => view.Status switch
    {
        ConversationStatus.Starting or ConversationStatus.Running => ChatStatus.Streaming,
        ConversationStatus.Stopped => ChatStatus.Stopped,
        ConversationStatus.Failed => ChatStatus.Error,
        _ => ChatStatus.Idle,
    };

    public static string? ErrorOf(ConversationView view) => view.Failure switch
    {
        null => null,
        { Kind: FailureKind.AgentError or FailureKind.TimedOut } failure => failure.Message,
        { Kind: FailureKind.Unreachable } => "Beacon's agent can't be reached. Check that it's running, then try again.",
        { Kind: FailureKind.Rejected, StatusCode: { } status } => $"Beacon's agent refused the request (HTTP {(int)status}). Start a new chat if it happens again.",
        _ => "The answer stopped unexpectedly. Try again.",
    };
}
