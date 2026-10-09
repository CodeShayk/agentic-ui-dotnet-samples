using Microsoft.Extensions.AI;

namespace Beacon.Client.Core;

/// <summary>
/// Turns a <see cref="ConversationView"/> back into the <see cref="ChatMessage"/> history the next
/// run sends. AG-UI agents keep nothing between runs, so the client resends the conversation every
/// time; deriving it from the view means a <c>MESSAGES_SNAPSHOT</c> that reconciled the transcript
/// has reconciled the history too.
/// </summary>
public static class ConversationHistory
{
    /// <summary>Builds the history to send: every message the agent should see, in order, each with its <see cref="ChatMessage.MessageId"/>.</summary>
    /// <param name="view">The conversation.</param>
    /// <returns>A new list the caller may extend (for example with an interrupt's answer).</returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A tool call goes back only with its result. A call cut off by Stop, a call awaiting
    /// approval or a frontend call nobody answered would leave the history invalid for most models.</item>
    /// <item>Partial text the user saw before pressing Stop goes back as it is.</item>
    /// <item>Activity messages never go back (the protocol forbids it). Reasoning messages can't:
    /// <c>AGUIChatClient</c> 1.0.0 has no <see cref="ChatMessage"/> form for them. Chapter 16 returns to this.</item>
    /// <item><c>system</c> and <c>developer</c> messages belong to the server and aren't echoed.</item>
    /// </list>
    /// </remarks>
    public static List<ChatMessage> ToChatMessages(ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        List<ChatMessage> history = [];
        HashSet<string> sentCalls = new(StringComparer.Ordinal);

        foreach (var message in view.Messages)
        {
            var chat = message switch
            {
                UserMessageView user => new ChatMessage(ChatRole.User, [.. TextOf(user.Text), .. user.Attachments]) { AuthorName = user.Name },
                AssistantMessageView assistant => Assistant(view, assistant, sentCalls),
                ToolMessageView tool when sentCalls.Contains(tool.ToolCallId) =>
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent(tool.ToolCallId, tool.Content)]),
                _ => null,
            };

            if (chat is null)
            {
                continue;
            }

            chat.MessageId = message.Id;
            if (message.SubagentRunId is { } owner)
            {
                // AGUIChatClient reads attribution back from this key when it builds the next RunAgentInput.
                chat.AdditionalProperties ??= [];
                chat.AdditionalProperties[ConversationReducer.SubagentRunIdKey] = owner;
            }

            history.Add(chat);
        }

        return history;
    }

    private static ChatMessage? Assistant(ConversationView view, AssistantMessageView message, HashSet<string> sentCalls)
    {
        List<AIContent> contents = [.. TextOf(message.Text)];

        foreach (var call in view.ToolCallsOf(message))
        {
            if (call is { Status: ToolCallStatus.Completed, Result: not null } && call.Name.Length > 0)
            {
                contents.Add(new FunctionCallContent(call.Id, call.Name, JsonValues.ToArguments(call.Arguments)));
                sentCalls.Add(call.Id);
            }
        }

        return contents.Count == 0 ? null : new ChatMessage(ChatRole.Assistant, contents);
    }

    private static IEnumerable<AIContent> TextOf(string text) =>
        text.Length == 0 ? [] : [new TextContent(text)];
}
