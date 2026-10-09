using System.Text;
using AGUI.Abstractions;

namespace Ch23.Beacon.Security.History;

/// <summary>
/// Folds the events a run sends into the messages a well-behaved client would build from them, so the
/// server's transcript holds exactly what the client was shown, no more and no less.
/// </summary>
/// <param name="knownCallIds">
/// Tool calls already in the transcript. A resumed run announces the approved call again (a closed call
/// may be reopened by an agreeing start); it is the same call, so it isn't recorded twice.
/// </param>
public sealed class TranscriptRecorder(IEnumerable<string> knownCallIds)
{
    private readonly HashSet<string> _knownCallIds = new(knownCallIds, StringComparer.Ordinal);
    private readonly List<AGUIMessage> _messages = [];
    private readonly Dictionary<string, AGUIAssistantMessage> _assistants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringBuilder> _text = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AGUIToolCall Call, StringBuilder Arguments)> _calls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _results = new(StringComparer.Ordinal);

    public void Observe(BaseEvent evt)
    {
        switch (evt)
        {
            case TextMessageStartEvent start:
                Assistant(start.MessageId);
                _text[start.MessageId] = new StringBuilder();
                break;

            case TextMessageContentEvent content when _text.TryGetValue(content.MessageId, out var text):
                text.Append(content.Delta);
                Assistant(content.MessageId).Content = text.ToString();
                break;

            case ToolCallStartEvent start when _knownCallIds.Contains(start.ToolCallId):
                break;

            case ToolCallStartEvent start:
                var call = new AGUIToolCall
                {
                    Id = start.ToolCallId,
                    Type = "function",
                    Function = new AGUIToolCallFunction { Name = start.ToolCallName, Arguments = string.Empty },
                };
                var owner = Assistant(start.ParentMessageId ?? start.ToolCallId);
                (owner.ToolCalls ??= []).Add(call);
                _calls[start.ToolCallId] = (call, new StringBuilder());
                break;

            case ToolCallArgsEvent args when _calls.TryGetValue(args.ToolCallId, out var pending):
                pending.Arguments.Append(args.Delta);
                pending.Call.Function.Arguments = pending.Arguments.ToString();
                break;

            case ToolCallResultEvent result:
                _messages.Add(new AGUIToolMessage
                {
                    Id = result.MessageId,
                    ToolCallId = result.ToolCallId,
                    Content = result.Content,
                });
                _results.Add(result.ToolCallId);
                break;
        }
    }

    /// <summary>The run's messages, without assistant messages that ended up with neither text nor calls.</summary>
    public IReadOnlyList<AGUIMessage> Messages =>
        [.. _messages.Where(m => m is not AGUIAssistantMessage { Content: null or "", ToolCalls: null or { Count: 0 } })];

    /// <summary>The name and argument JSON of a call this run made, if it made it.</summary>
    public bool TryGetCall(string toolCallId, out string name, out string argumentsJson)
    {
        if (_calls.TryGetValue(toolCallId, out var pending))
        {
            name = pending.Call.Function.Name;
            argumentsJson = pending.Arguments.Length > 0 ? pending.Arguments.ToString() : "{}";
            return true;
        }

        name = argumentsJson = string.Empty;
        return false;
    }

    /// <summary>Calls this run made that got no result in the run.</summary>
    public IEnumerable<(string CallId, string Name)> UnansweredCalls =>
        _calls.Where(c => !_results.Contains(c.Key)).Select(c => (c.Key, c.Value.Call.Function.Name));

    private AGUIAssistantMessage Assistant(string id)
    {
        if (!_assistants.TryGetValue(id, out var message))
        {
            message = new AGUIAssistantMessage { Id = id };
            _assistants[id] = message;
            _messages.Add(message);
        }

        return message;
    }
}
