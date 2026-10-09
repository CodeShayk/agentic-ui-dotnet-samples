using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ch02.HandBuilt.Console;

/// <summary>How a run ended, as far as this client could tell.</summary>
public enum RunStatus
{
    /// <summary>RUN_FINISHED arrived.</summary>
    Finished,

    /// <summary>RUN_ERROR arrived: the server reported its own failure.</summary>
    Failed,

    /// <summary>The user stopped the run; the client closed the connection.</summary>
    Stopped,

    /// <summary>The stream ended without a terminal event.</summary>
    Truncated,

    /// <summary>The server refused the request with an HTTP error before the run started.</summary>
    Rejected,
}

public sealed record RunResult(RunStatus Status, string? Detail = null);

/// <summary>
/// One AG-UI thread with the agent. The server keeps nothing between runs, so this class holds
/// the conversation and sends all of it with every run.
/// </summary>
public sealed class AgentConversation(HttpClient http, Uri endpoint)
{
    private readonly List<InputMessage> _history = [];

    /// <summary>Minted once; every run in this conversation carries it.</summary>
    public string ThreadId { get; } = NewId("thread");

    public IReadOnlyList<InputMessage> History => _history;

    public async Task<RunResult> SendAsync(string question, Action<string> onDelta, CancellationToken cancellationToken)
    {
        _history.Add(new InputMessage(NewId("user"), "user", question));

        var input = new RunAgentInput
        {
            ThreadId = ThreadId,
            RunId = NewId("run"),      // a new run ID for every request
            Messages = [.. _history],  // the whole conversation, every time
            ProtocolVersion = "1.0",
        };

        string? messageId = null;
        var answer = new StringBuilder();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(input, ClientJsonContext.Default.RunAgentInput),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            // ResponseHeadersRead hands us the body while it is still arriving; without it,
            // HttpClient would buffer the whole run before returning.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Rejected before the run started: an HTTP status and no stream.
                return new RunResult(RunStatus.Rejected, $"{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var parser = SseParser.Create(body, static (_, data) =>
                JsonSerializer.Deserialize(data, ClientJsonContext.Default.WireEvent)!);

            RunResult? outcome = null;
            await foreach (var item in parser.EnumerateAsync(cancellationToken))
            {
                var evt = item.Data;
                switch (evt.Type)
                {
                    case "RUN_STARTED":
                        // A new run on the same stream: its own verdict starts unknown.
                        outcome = null;
                        break;

                    case "TEXT_MESSAGE_START":
                        messageId = evt.MessageId;
                        break;

                    case "TEXT_MESSAGE_CONTENT":
                        answer.Append(evt.Delta);
                        onDelta(evt.Delta ?? string.Empty);
                        break;

                    case "TEXT_MESSAGE_END":
                        RememberAnswer();
                        break;

                    case "RUN_FINISHED":
                        outcome = new RunResult(RunStatus.Finished);
                        break;

                    case "RUN_ERROR":
                        outcome = new RunResult(RunStatus.Failed, evt.Message);
                        break;

                    // Other event types, a replayed run's MESSAGES_SNAPSHOT included, are ignored for now.
                }
            }

            // The server closes the body after the last run's terminal event. A body that ends
            // without one is a truncated run, which a client must never report as a success.
            return outcome ?? new RunResult(RunStatus.Truncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RunResult(RunStatus.Stopped);
        }
        finally
        {
            // Text from a stopped, failed or truncated run stays in the history: it's what the user saw.
            RememberAnswer();
        }

        void RememberAnswer()
        {
            if (messageId is not null && answer.Length > 0)
            {
                // The assistant message keeps the ID the server gave it on TEXT_MESSAGE_START.
                _history.Add(new InputMessage(messageId, "assistant", answer.ToString()));
            }

            messageId = null;
            answer.Clear();
        }
    }

    private static string NewId(string prefix) => $"{prefix}-{RandomNumberGenerator.GetHexString(16, lowercase: true)}";
}
