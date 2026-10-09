using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Ch22.Beacon.Agent.Runs;

/// <summary>
/// The server's half of encrypted reasoning, from the store. <c>AGUI.Server</c> sends a provider's sealed reasoning
/// out as <c>REASONING_ENCRYPTED_VALUE</c> but can't take it back (Chapter 16), and a server that keeps the
/// conversation doesn't need the client to return it: the run records already hold every value, on the reasoning
/// message it rode. So reasoning messages a client sends are ignored, and the values come from the branch.
/// </summary>
public static class SealedReasoning
{
    /// <summary>
    /// Puts each sealed value on the branch back where the model produced it: at the start of the next message
    /// the model sees, as <see cref="TextReasoningContent.ProtectedData"/>, in an assistant message of its own when
    /// that next message isn't the assistant's. Values before messages the model no longer sees (summarized by
    /// compaction) stay in the store and aren't sent.
    /// </summary>
    /// <param name="messages">The model's messages, converted from the server's history.</param>
    /// <param name="branch">The server's record of the branch and the run's new messages, in order, reasoning included.</param>
    /// <returns>How many values were restored.</returns>
    public static int Restore(List<ChatMessage> messages, IEnumerable<AGUIMessage> branch)
    {
        var restored = 0;
        List<string> pending = [];
        foreach (var message in branch)
        {
            if (message is AGUIReasoningMessage reasoning)
            {
                if (reasoning.EncryptedValue is { Length: > 0 } value)
                {
                    pending.Add(value);
                }

                continue;
            }

            if (pending.Count == 0)
            {
                continue;
            }

            var index = messages.FindIndex(m => m.MessageId == message.Id);
            if (index >= 0)
            {
                IEnumerable<AIContent> values = pending.Select(v => new TextReasoningContent(null) { ProtectedData = v });
                if (messages[index].Role == ChatRole.Assistant)
                {
                    messages[index].Contents = [.. values, .. messages[index].Contents];
                }
                else
                {
                    messages.Insert(index, new ChatMessage(ChatRole.Assistant, [.. values]));
                }

                restored += pending.Count;
            }

            pending.Clear();
        }

        return restored;
    }
}
