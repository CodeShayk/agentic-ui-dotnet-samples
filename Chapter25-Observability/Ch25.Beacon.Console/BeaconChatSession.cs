using System.Diagnostics;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Ch25.Beacon.Console;

/// <summary>What one turn produced, including the trace that recorded it.</summary>
public sealed record ChatTurn(
    string Text,
    string? ThreadId,
    string? RunId,
    IReadOnlyList<(string? Model, UsageDetails Usage)> Usage,
    string? Error,
    string? TraceId);

/// <summary>
/// A conversation with Beacon over AG-UI, adapted from Chapter 4: full history every turn and one
/// <see cref="ChatOptions"/> for the thread. Each turn runs inside a <c>beacon.console.ask</c> span,
/// so the client, the HTTP call and the agent's spans share one trace ID the user can quote.
/// </summary>
public sealed class BeaconChatSession(IChatClient client)
{
    private readonly List<ChatMessage> _history = [];
    private readonly ChatOptions _options = new();

    public async Task<ChatTurn> SendAsync(string text, Action<string>? onText = null, CancellationToken cancellationToken = default)
    {
        using var ask = ClientTelemetry.Source.StartActivity("beacon.console.ask", ActivityKind.Internal);

        _history.Add(new ChatMessage(ChatRole.User, text) { MessageId = $"user_{Guid.NewGuid():N}" });

        List<ChatResponseUpdate> updates = [];
        RunStartedEvent? started = null;
        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(_history, _options, cancellationToken))
            {
                updates.Add(update);
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
            _history.RemoveAt(_history.Count - 1);
            throw;
        }

        ask?.SetTag("agui.thread_id", started?.ThreadId);
        ask?.SetTag("agui.run_id", started?.RunId);

        var error = updates.SelectMany(u => u.Contents).OfType<ErrorContent>().FirstOrDefault();
        if (error is null)
        {
            _history.AddMessages(updates);
        }
        else
        {
            _history.RemoveAt(_history.Count - 1);
            ask?.SetStatus(ActivityStatusCode.Error, error.Message);
        }

        // AGUIChatClient turns RUN_FINISHED's usage into UsageContent, one per provider and model.
        var usage = updates
            .SelectMany(u => u.Contents.OfType<UsageContent>().Select(c => (u.ModelId, c.Details)))
            .ToList();

        return new ChatTurn(
            Text: string.Concat(updates.Select(u => u.Text)),
            ThreadId: started?.ThreadId,
            RunId: started?.RunId,
            Usage: usage,
            Error: error?.Message,
            TraceId: ask?.TraceId.ToString());
    }
}
