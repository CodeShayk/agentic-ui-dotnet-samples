using Beacon.Client.Core;
using Beacon.Desk.Ui;
using Ch20.DeclarativeUi;

namespace Ch20.Beacon.Desk.Components.Chat;

/// <summary>
/// What the chat panel shows, projected from the conversation's view: user and assistant text, and each
/// <c>a2ui-surface</c> activity as a surface in the place it arrived. Projecting also brings the surface
/// board up to date, so every surface is validated once, when its content first appears.
/// </summary>
public sealed record DeskTranscript(IReadOnlyList<TranscriptMessage> Messages, TranscriptMessage? Streaming)
{
    /// <summary>Surface entries are transcript entries with this prefix on the activity message's ID.</summary>
    public const string SurfacePrefix = "surface:";

    public static DeskTranscript From(ConversationView view, SurfaceBoard board)
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

                case AssistantMessageView { Text.Length: > 0 } assistant:
                    var shown = new TranscriptMessage(assistant.Id, ChatAuthor.Assistant, assistant.Text) { IsPartial = assistant.IsPartial };
                    if (assistant.IsStreaming)
                    {
                        if (streaming is not null)
                        {
                            messages.Add(streaming);
                        }

                        streaming = shown;
                    }
                    else
                    {
                        messages.Add(shown);
                    }

                    break;

                // The text is what screen readers hear when the surface appears; SurfaceView draws the rest.
                // Other activity types are tolerated and skipped, as the spec asks of a consumer.
                case ActivityMessageView activity when SurfaceActivity.IsSurface(activity.ActivityType):
                    var surface = board.Sync(activity.Id, activity.Content);
                    messages.Add(new TranscriptMessage(SurfacePrefix + activity.Id, ChatAuthor.Assistant, Describe(surface)));
                    break;
            }
        }

        return new DeskTranscript(messages, streaming);
    }

    private static string Describe(SurfaceInstance surface) => surface.Verdict.Outcome switch
    {
        SurfaceOutcome.Rejected => "Beacon sent a form this Desk won't show.",
        SurfaceOutcome.Degraded => $"Form: {surface.Title}. Part of it needs a newer Desk.",
        _ => $"Form: {surface.Title}.",
    };
}
