using AGUI.Abstractions;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch16.Beacon.Desk.Reasoning;

/// <summary>
/// The client's half of the encrypted round trip. AG-UI says a client stores each <c>REASONING_ENCRYPTED_VALUE</c>
/// with its message and returns it unmodified in later input. <c>AGUIChatClient</c> 1.0.0 builds
/// <c>RunAgentInput.messages</c> from <see cref="ChatMessage"/> history, which has no place for a reasoning
/// message, so this writes the messages itself, through <see cref="ConversationOptions.ConfigureInput"/>.
/// </summary>
public static class ReasoningRoundTrip
{
    /// <summary>
    /// Use as <see cref="ConversationOptions.ConfigureInput"/>. When the conversation holds sealed reasoning,
    /// the run's messages become Chapter 7's history with each reasoning message that carries a value back
    /// in its place; otherwise the input is left alone.
    /// </summary>
    /// <remarks>
    /// The history comes from the view, so this suits turns started with <see cref="AgentConversation.SendAsync(string, RunInputs?, CancellationToken)"/>
    /// and <see cref="AgentConversation.ContinueAsync"/>. A frontend tool's follow-up run or an interrupt answer adds
    /// messages the view doesn't hold yet; a client with either needs to merge rather than rebuild.
    /// </remarks>
    public static void ConfigureInput(RunAgentInput input, ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(view);

        if (!view.Messages.Any(m => m is ReasoningMessageView { EncryptedValue: not null }))
        {
            return; // nothing sealed to return: AGUIChatClient converts the history as usual
        }

        input.Messages = ToAGUIMessages(view);
    }

    /// <summary>Chapter 7's history as AG-UI messages, with the sealed reasoning messages where they arrived.</summary>
    public static List<AGUIMessage> ToAGUIMessages(ConversationView view)
    {
        Dictionary<string, ChatMessage> history = new(StringComparer.Ordinal);
        foreach (var message in ConversationHistory.ToChatMessages(view))
        {
            history.TryAdd(message.MessageId!, message);
        }

        List<AGUIMessage> messages = [];
        foreach (var message in view.Messages)
        {
            if (message is ReasoningMessageView { EncryptedValue: { } sealedValue } reasoning)
            {
                // Returned as it arrived: the value unmodified, with the text the client was shown.
                messages.Add(new AGUIReasoningMessage
                {
                    Id = reasoning.Id,
                    Content = reasoning.Text,
                    EncryptedValue = sealedValue,
                    SubagentRunId = reasoning.SubagentRunId,
                });
            }
            else if (history.TryGetValue(message.Id, out var chat))
            {
                messages.AddRange(new[] { chat }.AsAGUIMessages(AGUIJsonSerializerContext.Default.Options));
            }
        }

        return messages;
    }
}
