using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Ch04.Beacon.Console;

/// <summary>What one turn of the conversation produced.</summary>
/// <param name="Text">The assistant's answer, as streamed.</param>
/// <param name="ThreadId">The AG-UI thread ID from <c>RUN_STARTED</c>.</param>
/// <param name="RunId">The AG-UI run ID from <c>RUN_STARTED</c>.</param>
/// <param name="MessagesSent">How many messages the turn sent: the whole history, every time.</param>
/// <param name="ConversationId">The first non-null <see cref="ChatResponseUpdate.ConversationId"/> seen (always null against AG-UI).</param>
/// <param name="Error">The <c>RUN_ERROR</c> message, if the run failed.</param>
public sealed record ChatTurn(
    string Text,
    string? ThreadId,
    string? RunId,
    int MessagesSent,
    string? ConversationId,
    string? Error);

/// <summary>
/// A conversation with Beacon over AG-UI. The server remembers nothing between runs, so the session
/// keeps the full history and one <see cref="ChatOptions"/> instance, which carries the thread ID
/// from turn to turn.
/// </summary>
public sealed class BeaconChatSession(IChatClient client)
{
    private readonly List<ChatMessage> _history = [];
    private readonly ChatOptions _options = new();

    public IReadOnlyList<ChatMessage> History => _history;

    public async Task<ChatTurn> SendAsync(
        string text,
        Action<string>? onText = null,
        CancellationToken cancellationToken = default)
    {
        // AGUIChatClient copies MessageId into the AG-UI message id, and an unset one goes out as "".
        _history.Add(new ChatMessage(ChatRole.User, text) { MessageId = $"user_{Guid.NewGuid():N}" });
        var messagesSent = _history.Count;

        List<ChatResponseUpdate> updates = [];
        RunStartedEvent? started = null;
        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(_history, _options, cancellationToken))
            {
                updates.Add(update);

                // Lifecycle events arrive as content-less updates with the AG-UI event attached.
                if (update.RawRepresentation is RunStartedEvent runStarted)
                {
                    started = runStarted;
                }

                if (update.Text is { Length: > 0 } delta)
                {
                    onText?.Invoke(delta);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A stopped turn leaves no trace, so the next turn isn't built on half an answer.
            _history.RemoveAt(_history.Count - 1);
            throw;
        }

        var error = updates.SelectMany(u => u.Contents).OfType<ErrorContent>().FirstOrDefault();
        if (error is null)
        {
            // Fold the streamed updates into assistant messages; the next turn sends them back.
            _history.AddMessages(updates);
        }
        else
        {
            _history.RemoveAt(_history.Count - 1);
        }

        return new ChatTurn(
            Text: string.Concat(updates.Select(u => u.Text)),
            ThreadId: started?.ThreadId,
            RunId: started?.RunId,
            MessagesSent: messagesSent,
            ConversationId: updates.Select(u => u.ConversationId).FirstOrDefault(id => id is not null),
            Error: error?.Message);
    }
}
