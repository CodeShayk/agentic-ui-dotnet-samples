using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Beacon.AI;
using Ch02.HandBuilt.Agent.Protocol;
using Microsoft.Extensions.AI;

namespace Ch02.HandBuilt.Agent;

/// <summary>
/// Turns one <see cref="RunAgentInput"/> into one AG-UI run: it calls the model and converts its
/// streamed text into RUN_STARTED, TEXT_MESSAGE_START/CONTENT/END and RUN_FINISHED, or RUN_ERROR
/// when the model call fails.
/// </summary>
public sealed class AgentRunner(IChatClient chatClient, ILogger<AgentRunner> logger)
{
    /// <summary>The AG-UI protocol version this server speaks, declared on RUN_STARTED.</summary>
    public const string ProtocolVersion = "1.0";

    public async IAsyncEnumerable<SseItem<AgentEvent>> StreamAsync(
        RunAgentInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Run {RunId} on thread {ThreadId} started with {Count} messages.",
            input.RunId, input.ThreadId, input.Messages.Count);

        // 1. Open the run, echoing the IDs the client chose.
        yield return Frame(new RunStartedEvent(input.ThreadId, input.RunId)
        {
            ParentRunId = input.ParentRunId,
            ProtocolVersion = ProtocolVersion,
        });

        // 2. Call the model with the whole conversation the client sent, and the client's
        //    cancellation token, so a client that goes away stops the model call too.
        await using var updates = chatClient
            .GetStreamingResponseAsync(ToChatMessages(input.Messages), cancellationToken: cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        string? messageId = null;
        var deltas = 0;
        var failed = false;

        while (true)
        {
            string text;
            try
            {
                if (!await updates.MoveNextAsync())
                {
                    break;
                }

                // Text only. Reasoning (Chapter 16) and tool calls (Chapter 9) are dropped for now.
                text = updates.Current.Text;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The client disconnected. Nobody is left to read an event, so stop quietly.
                logger.LogInformation("Run {RunId} cancelled by the client after {Deltas} deltas.", input.RunId, deltas);
                yield break;
            }
            catch (Exception exception)
            {
                // The 200 and RUN_STARTED are already on the wire: report the failure in-band.
                logger.LogError(exception, "Run {RunId} failed in the model call.", input.RunId);
                failed = true;
                break;
            }

            if (text.Length == 0)
            {
                continue;
            }

            // 3. Open the assistant message on the first piece of text, then stream deltas into it.
            if (messageId is null)
            {
                messageId = NewMessageId();
                yield return Frame(new TextMessageStartEvent(messageId));
            }

            deltas++;
            yield return Frame(new TextMessageContentEvent(messageId, text));
        }

        if (failed)
        {
            // Keep exception details in the server log; the client gets a stable, safe message.
            yield return Frame(new RunErrorEvent("Beacon couldn't finish this answer. Please try again.")
            {
                Code = "MODEL_ERROR",
            });
            yield break;
        }

        // 4. Close what the run opened, then the run itself.
        if (messageId is not null)
        {
            yield return Frame(new TextMessageEndEvent(messageId));
        }

        yield return Frame(new RunFinishedEvent(input.ThreadId, input.RunId));
        logger.LogInformation("Run {RunId} finished after {Deltas} deltas.", input.RunId, deltas);
    }

    private static SseItem<AgentEvent> Frame(AgentEvent evt) => new(evt);

    // The server mints message IDs rather than trusting the model's: providers differ in whether
    // and how they number messages, and a repeated ID would reopen an earlier message in the thread.
    private static string NewMessageId() => $"msg-{RandomNumberGenerator.GetHexString(16, lowercase: true)}";

    /// <summary>
    /// Maps the client's history to Microsoft.Extensions.AI messages. The server owns its
    /// instructions, so it adds its own system message and ignores client-sent system and
    /// developer messages (Chapter 5 revisits this decision).
    /// </summary>
    private static List<ChatMessage> ToChatMessages(IReadOnlyList<InputMessage> history)
    {
        List<ChatMessage> messages = [new(ChatRole.System, BeaconInstructions.Default)];

        foreach (var message in history)
        {
            ChatRole? role = message.Role switch
            {
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                _ => null, // system, developer, tool, activity, reasoning: not used yet
            };

            if (role is { } knownRole && message.GetText() is { Length: > 0 } text)
            {
                messages.Add(new ChatMessage(knownRole, text) { MessageId = message.Id });
            }
        }

        return messages;
    }
}
