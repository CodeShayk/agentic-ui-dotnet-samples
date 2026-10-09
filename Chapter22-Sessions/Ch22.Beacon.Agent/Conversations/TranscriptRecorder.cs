using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.Client.Core;

namespace Ch22.Beacon.Agent.Conversations;

/// <summary>
/// Folds one run's events into the messages a client builds from them, plus the state the run ended with
/// and how it ended. The server records exactly what it showed: the text that went out, the calls and
/// their arguments as streamed, the results and the reasoning, under the IDs the client knows them by.
/// A sealed reasoning value (<c>REASONING_ENCRYPTED_VALUE</c>) is kept with the message or call it names,
/// so the server holds the authoritative copy and can restore it on the next turn.
/// </summary>
/// <param name="startState">The state the run started from (the input's <c>state</c>).</param>
/// <param name="knownCallIds">Calls already on the branch; a stream that announces one again is restating it.</param>
public sealed class TranscriptRecorder(JsonElement? startState, IEnumerable<string> knownCallIds)
{
    private readonly HashSet<string> _knownCallIds = new(knownCallIds, StringComparer.Ordinal);
    private readonly List<AGUIMessage> _messages = [];
    private readonly Dictionary<string, AGUIAssistantMessage> _assistants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringBuilder> _text = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AGUIToolCall Call, StringBuilder Arguments)> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AGUIReasoningMessage> _reasoning = new(StringComparer.Ordinal);
    private JsonNode? _state = startState is { } s ? JsonNode.Parse(s.GetRawText()) : null;

    public void Observe(BaseEvent evt)
    {
        switch (evt)
        {
            case TextMessageStartEvent start:
                Assistant(start.MessageId);
                break;

            case TextMessageContentEvent content:
                var text = _text.TryGetValue(content.MessageId, out var existing) ? existing : _text[content.MessageId] = new StringBuilder();
                text.Append(content.Delta);
                Assistant(content.MessageId).Content = text.ToString();
                break;

            case ToolCallStartEvent start when !_knownCallIds.Contains(start.ToolCallId) && !_calls.ContainsKey(start.ToolCallId):
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
                _messages.Add(new AGUIToolMessage { Id = result.MessageId, ToolCallId = result.ToolCallId, Content = result.Content });
                break;

            case ReasoningMessageStartEvent start:
                Reasoning(start.MessageId);
                break;

            case ReasoningMessageContentEvent content:
                var reasoning = Reasoning(content.MessageId);
                reasoning.Content += content.Delta;
                break;

            case ReasoningEncryptedValueEvent sealedValue:
                Seal(sealedValue);
                break;

            case RunStartedEvent started:
                ProtocolVersion ??= started.ProtocolVersion;
                break;

            case StateSnapshotEvent snapshot:
                _state = JsonNode.Parse(snapshot.Snapshot.GetRawText());
                break;

            case StateDeltaEvent delta:
                // A patch that doesn't apply changes nothing, on the client and here alike (Chapter 12).
                var patched = JsonPatch.Apply(_state, delta.Delta);
                if (patched.Succeeded)
                {
                    _state = patched.Document;
                }

                break;

            case RunFinishedEvent finished:
                Outcome = finished.Outcome ?? new RunFinishedSuccessOutcome();
                break;

            case RunErrorEvent error:
                (Error, ErrorCode) = (error.Message, error.Code);
                break;

            // MESSAGES_SNAPSHOT and CUSTOM events in a run's stream are the server's own restatements; they add no messages.
        }
    }

    /// <summary>The run's messages, without assistant messages that ended up with neither text nor calls.</summary>
    public IReadOnlyList<AGUIMessage> Messages =>
        [.. _messages.Where(m => m is not AGUIAssistantMessage { Content: null or "", ToolCalls: null or { Count: 0 } })];

    public JsonElement? State => _state is null ? null : ToElement(_state);

    public RunFinishedOutcome? Outcome { get; private set; }

    public string? Error { get; private set; }

    public string? ErrorCode { get; private set; }

    /// <summary>The version the run's <c>RUN_STARTED</c> declared, if it declared one.</summary>
    public string? ProtocolVersion { get; private set; }

    /// <summary>How the run ended, or <see langword="null"/> while no terminal event has been seen.</summary>
    public RunEnding? Ending => (Outcome, Error) switch
    {
        (not null, _) => RunEnding.Finished,
        (_, not null) => RunEnding.Failed,
        _ => null,
    };

    // REASONING_ENCRYPTED_VALUE: the value rides the message or call it names. AGUI.Server can name a reasoning
    // message before (or without) opening it, so an unknown message ID becomes a reasoning message of its own,
    // which a later REASONING_MESSAGE_START with that ID continues. A call this run didn't make is skipped.
    private void Seal(ReasoningEncryptedValueEvent sealedValue)
    {
        if (sealedValue.Subtype == "tool-call")
        {
            if (_calls.TryGetValue(sealedValue.EntityId, out var pending))
            {
                pending.Call.EncryptedValue = sealedValue.EncryptedValue;
            }

            return;
        }

        switch (_messages.FirstOrDefault(m => m.Id == sealedValue.EntityId))
        {
            case AGUIAssistantMessage assistant:
                assistant.EncryptedValue = sealedValue.EncryptedValue;
                break;
            case AGUIToolMessage tool:
                tool.EncryptedValue = sealedValue.EncryptedValue;
                break;
            default:
                Reasoning(sealedValue.EntityId).EncryptedValue = sealedValue.EncryptedValue;
                break;
        }
    }

    private static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

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

    private AGUIReasoningMessage Reasoning(string id)
    {
        if (!_reasoning.TryGetValue(id, out var message))
        {
            message = new AGUIReasoningMessage { Id = id, Content = string.Empty };
            _reasoning[id] = message;
            _messages.Add(message);
        }

        return message;
    }
}
