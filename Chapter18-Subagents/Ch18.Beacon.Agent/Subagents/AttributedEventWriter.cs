using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Ch18.Beacon.Agent.Subagents;

/// <summary>
/// Writes what one subagent invocation says and does as AG-UI events attributed to it: text messages,
/// tool calls and tool results, each carrying <c>subagentRunId</c>. <c>AGUI.Server</c>'s conversion never
/// sets that field, so the coordinator converts a subagent's stream itself and hands the events to the
/// SDK through <see cref="ChatResponseUpdate.RawRepresentation"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every event is tagged, not only the openers. The specialists stream at the same time, and a consumer
/// that sees a tag on every event never has to work out an owner from what came before.
/// </para>
/// <para>
/// A tool call is announced once. When a suspended invocation continues, function invocation restates
/// the approved call under a new message ID; the calls announced in the earlier run are passed in, so the
/// continuation reports only the call's result. Reasoning stays on the server, and usage isn't an event:
/// the coordinator forwards it, so the run's total on <c>RUN_FINISHED</c> includes the specialists' calls.
/// </para>
/// </remarks>
/// <param name="subagentRunId">The invocation every event is attributed to.</param>
/// <param name="announcedCalls">Tool calls already on the wire from an earlier run of this invocation.</param>
public sealed class AttributedEventWriter(string subagentRunId, IEnumerable<string>? announcedCalls = null)
{
    // Arguments and results are JSON inside a JSON string; the event's own serializer escapes them for the wire.
    private static readonly JsonWriterOptions Inner = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HashSet<string> _announced = new(announcedCalls ?? [], StringComparer.Ordinal);
    private readonly HashSet<string> _answered = new(StringComparer.Ordinal);
    private readonly List<string> _interruptIds = [];
    private readonly HashSet<string> _awaitingApproval = new(StringComparer.Ordinal);
    private string? _openMessageId;

    /// <summary>The invocation this writer attributes events to.</summary>
    public string SubagentRunId { get; } = subagentRunId;

    /// <summary>The approvals the subagent asked for. Any at all means the invocation is suspended, not finished.</summary>
    public IReadOnlyList<string> InterruptIds => _interruptIds;

    /// <summary>The events for one update from the subagent's pipeline, in order.</summary>
    public IEnumerable<BaseEvent> Write(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent { Text.Length: > 0 } text:
                    var messageId = update.MessageId ?? _openMessageId ?? $"msg_{Guid.NewGuid():N}";
                    if (messageId != _openMessageId)
                    {
                        if (CloseMessage() is { } previous)
                        {
                            yield return previous;
                        }

                        _openMessageId = messageId;
                        yield return new TextMessageStartEvent { MessageId = messageId, Role = AGUIRoles.Assistant, SubagentRunId = SubagentRunId };
                    }

                    yield return new TextMessageContentEvent { MessageId = messageId, Delta = text.Text, SubagentRunId = SubagentRunId };
                    break;

                case FunctionCallContent call:
                    foreach (var evt in Announce(call, update.MessageId))
                    {
                        yield return evt;
                    }

                    break;

                // A call the spending policy held back: on the wire it's an ordinary call, and the
                // interrupt that asks about it goes on RUN_FINISHED.
                case ToolApprovalRequestContent { ToolCall: FunctionCallContent call } approval:
                    foreach (var evt in Announce(call, update.MessageId))
                    {
                        yield return evt;
                    }

                    _interruptIds.Add(approval.RequestId);
                    _awaitingApproval.Add(call.CallId);
                    break;

                case FunctionResultContent result:
                    if (CloseMessage() is { } beforeResult)
                    {
                        yield return beforeResult;
                    }

                    _answered.Add(result.CallId);
                    yield return Result(result.CallId, ResultText(result));
                    break;
            }
        }
    }

    /// <summary>
    /// Closes whatever is still open when the invocation stops. A call that was running when the
    /// invocation failed gets a result saying so: without one, a client would wait for it forever.
    /// </summary>
    /// <param name="abandonedBecause">Why the invocation stopped early; <see langword="null"/> when it ended normally.</param>
    public IEnumerable<BaseEvent> Complete(string? abandonedBecause = null)
    {
        if (CloseMessage() is { } last)
        {
            yield return last;
        }

        if (abandonedBecause is null)
        {
            yield break;
        }

        foreach (var callId in _announced.Where(id => !_answered.Contains(id) && !_awaitingApproval.Contains(id)).ToList())
        {
            _answered.Add(callId);
            yield return Result(callId, ErrorJson($"The call was abandoned. {abandonedBecause}"));
        }
    }

    private IEnumerable<BaseEvent> Announce(FunctionCallContent call, string? messageId)
    {
        if (!_announced.Add(call.CallId))
        {
            yield break; // already on the wire: a continuation restating an approved call
        }

        if (CloseMessage() is { } beforeCall)
        {
            yield return beforeCall;
        }

        // The call belongs to the subagent's message that carried it, so its parent is attributed to the same owner.
        yield return new ToolCallStartEvent { ToolCallId = call.CallId, ToolCallName = call.Name, ParentMessageId = messageId, SubagentRunId = SubagentRunId };
        yield return new ToolCallArgsEvent { ToolCallId = call.CallId, Delta = Arguments(call), SubagentRunId = SubagentRunId };
        yield return new ToolCallEndEvent { ToolCallId = call.CallId, SubagentRunId = SubagentRunId };
    }

    // The subagent's own function invocation ran the tool, so the result is attributed to it too.
    private ToolCallResultEvent Result(string callId, string content) =>
        new() { MessageId = callId, ToolCallId = callId, Content = content, SubagentRunId = SubagentRunId };

    private TextMessageEndEvent? CloseMessage()
    {
        if (_openMessageId is not { } open)
        {
            return null;
        }

        _openMessageId = null;
        return new TextMessageEndEvent { MessageId = open, SubagentRunId = SubagentRunId };
    }

    private static string Arguments(FunctionCallContent call)
    {
        // Written through a plain Utf8JsonWriter, so the arguments go out on one line whatever the options say.
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Inner))
        {
            JsonSerializer.Serialize(writer, call.Arguments ?? new Dictionary<string, object?>(),
                AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, object?>)));
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // Tool results arrive as JsonElement, written indented by Microsoft.Extensions.AI's defaults; the wire gets them compact.
    private static string ResultText(FunctionResultContent result) => result.Result switch
    {
        JsonElement json => Compact(json),
        string text => text,
        null when result.Exception is not null => ErrorJson("The tool failed."),
        null => string.Empty,
        var other => other.ToString() ?? string.Empty,
    };

    private static string ErrorJson(string message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Inner))
        {
            writer.WriteStartObject();
            writer.WriteString("error", message);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Compact(JsonElement json)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Inner))
        {
            json.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
