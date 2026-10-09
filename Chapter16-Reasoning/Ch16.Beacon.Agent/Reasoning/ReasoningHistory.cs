using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Ch16.Beacon.Agent.Reasoning;

/// <summary>A sealed value a client returned, and the message the model produced it with.</summary>
/// <param name="ReasoningMessageId">The reasoning message that carried it.</param>
/// <param name="EncryptedValue">The value, exactly as the client sent it.</param>
/// <param name="FollowedBy">The ID of the next message in the history, or <see langword="null"/> if it came last.</param>
public sealed record CarriedReasoning(string ReasoningMessageId, string EncryptedValue, string? FollowedBy);

/// <summary>
/// The server's half of the encrypted round trip. <c>AGUI.Server</c> 1.0.0 maps <c>TextReasoningContent.ProtectedData</c>
/// out to <c>REASONING_ENCRYPTED_VALUE</c> but not back: its message conversion throws on the <c>reasoning</c>
/// role and ignores <c>encryptedValue</c>. So reasoning messages are taken out of the input before the SDK
/// converts it and put back, as the provider's own content, afterward.
/// </summary>
public static class ReasoningHistory
{
    /// <summary>The largest value accepted. Real providers' artifacts are a few kilobytes.</summary>
    public const int MaxEncryptedValueLength = 64 * 1024;

    /// <summary>Takes the reasoning messages out of <paramref name="input"/>, keeping only their sealed values.</summary>
    /// <returns>The input without reasoning messages, the values to restore, and an error if a value is too large.</returns>
    public static (RunAgentInput Remaining, IReadOnlyList<CarriedReasoning> Carried, string? Error) Split(RunAgentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        List<AGUIMessage> rest = [];
        List<(AGUIReasoningMessage Message, int Before)> found = [];
        foreach (var message in input.Messages)
        {
            if (message is AGUIReasoningMessage reasoning)
            {
                // The client's copy of the reasoning text is never used: anyone can write a "reasoning" message,
                // and text in that role would steer the model. Only the provider's sealed value goes back.
                if (reasoning.EncryptedValue is { Length: > 0 } value)
                {
                    if (value.Length > MaxEncryptedValueLength)
                    {
                        return (input, [], $"The encrypted value on reasoning message '{reasoning.Id}' is too large.");
                    }

                    found.Add((reasoning, rest.Count));
                }

                continue;
            }

            rest.Add(message);
        }

        List<CarriedReasoning> carried =
        [
            .. found.Select(f => new CarriedReasoning(
                f.Message.Id, f.Message.EncryptedValue!, f.Before < rest.Count ? rest[f.Before].Id : null)),
        ];

        return (Copy(input, rest), carried, null);
    }

    /// <summary>
    /// Puts each sealed value back where the model produced it: at the start of the assistant message that
    /// followed its reasoning, as <see cref="TextReasoningContent"/> with <see cref="TextReasoningContent.ProtectedData"/>.
    /// </summary>
    public static void Restore(List<ChatMessage> messages, IReadOnlyList<CarriedReasoning> carried)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(carried);

        // Last first, inserting in front, so several values before one message keep their order.
        var end = messages.Count;
        for (var i = carried.Count - 1; i >= 0; i--)
        {
            var value = carried[i];
            var reasoning = new TextReasoningContent(null) { ProtectedData = value.EncryptedValue };
            var index = value.FollowedBy is null ? -1 : messages.FindIndex(m => m.MessageId == value.FollowedBy);

            if (index >= 0 && messages[index].Role == ChatRole.Assistant)
            {
                messages[index].Contents.Insert(0, reasoning);
            }
            else
            {
                var standalone = new ChatMessage(ChatRole.Assistant, [reasoning]) { MessageId = value.ReasoningMessageId };
                messages.Insert(index >= 0 ? index : end, standalone);
            }
        }
    }

    private static RunAgentInput Copy(RunAgentInput input, IList<AGUIMessage> messages) => new()
    {
        ThreadId = input.ThreadId,
        RunId = input.RunId,
        ParentRunId = input.ParentRunId,
        ProtocolVersion = input.ProtocolVersion,
        Messages = messages,
        Tools = input.Tools,
        Context = input.Context,
        State = input.State,
        ForwardedProperties = input.ForwardedProperties,
        Resume = input.Resume,
    };
}
