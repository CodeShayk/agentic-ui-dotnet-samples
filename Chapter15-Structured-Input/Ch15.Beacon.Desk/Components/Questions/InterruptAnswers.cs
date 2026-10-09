using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Ch15.Forms;

namespace Ch15.Beacon.Desk.Components.Questions;

/// <summary>Sends Dana's answers to the agent through the conversation, as one resume covering every open question.</summary>
public static class InterruptAnswers
{
    public static Task SendAsync(AgentConversation conversation, QuestionAnswers decided, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        // Envelope information about the answers, not part of them: which client sent them, and when.
        var metadata = Metadata(clock);

        if (decided.Abandoned.Count == 0)
        {
            // Every question answered: the SDK's own shape. RespondAsync puts each request and its
            // InterruptResponseContent at the end of the history, and AGUIChatClient turns the pairs into
            // resume entries with the resolved status.
            return conversation.RespondAsync(
                [.. decided.Answers.Select(a => new InterruptResponseContent(a.InterruptId) { Payload = a.Payload, Metadata = metadata })],
                cancellationToken: cancellationToken);
        }

        // InterruptResponseContent can only resolve. To abandon one question and answer another, the Desk writes
        // the whole resume list itself, one entry per open interrupt, because omission is not abandonment.
        return conversation.ContinueAsync(new RunInputs
        {
            Resume =
            [
                .. decided.Answers.Select(a => new AGUIResume
                {
                    InterruptId = a.InterruptId,
                    Status = ResumeStatus.Resolved,
                    Payload = a.Payload,
                    Metadata = metadata,
                }),
                .. decided.Abandoned.Select(id => new AGUIResume { InterruptId = id, Status = ResumeStatus.Cancelled, Metadata = metadata }),
            ],
        }, cancellationToken);
    }

    private static JsonElement Metadata(TimeProvider clock)
    {
        var metadata = new JsonObject
        {
            ["beacon"] = new JsonObject
            {
                ["client"] = "beacon-desk",
                ["answeredAt"] = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            },
        };

        using var document = JsonDocument.Parse(metadata.ToJsonString());
        return document.RootElement.Clone();
    }
}
