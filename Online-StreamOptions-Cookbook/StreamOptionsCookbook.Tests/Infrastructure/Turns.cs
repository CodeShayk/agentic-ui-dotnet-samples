using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace StreamOptionsCookbook.Tests.Infrastructure;

/// <summary>Recorded model turns, shaped the way real providers report them.</summary>
internal static class Turns
{
    public const string Model = "beacon-scripted";

    /// <summary>A turn that calls one tool. Arguments are parsed into <see cref="JsonElement"/> values, as providers do.</summary>
    public static IReadOnlyList<ChatResponseUpdate> Call(string callId, string name, string argumentsJson, string messageId = "msg-1") =>
    [
        new(ChatRole.Assistant, [FunctionCallContent.CreateFromParsedArguments(argumentsJson, callId, name, ParseArguments)])
        {
            MessageId = messageId,
            ModelId = Model,
        },
    ];

    /// <summary>A text turn streamed in the given chunks.</summary>
    public static IReadOnlyList<ChatResponseUpdate> Text(string messageId, params string[] chunks) =>
        [.. chunks.Select(chunk => new ChatResponseUpdate(ChatRole.Assistant, chunk) { MessageId = messageId, ModelId = Model })];

    /// <summary>
    /// A tool call streamed the way Microsoft.Extensions.AI's OpenAI client streams it: one content-less update
    /// per chunk with the OpenAI SDK's chunk as <see cref="ChatResponseUpdate.RawRepresentation"/>, then the
    /// coalesced <see cref="FunctionCallContent"/> once the call is complete.
    /// </summary>
    public static IReadOnlyList<ChatResponseUpdate> StreamedCall(string callId, string name, params string[] argumentPieces)
    {
        var updates = argumentPieces.Select((piece, i) => new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            MessageId = "msg-1",
            ModelId = Model,
            RawRepresentation = OpenAIChatModelFactory.StreamingChatCompletionUpdate(toolCallUpdates:
            [
                i == 0
                    ? OpenAIChatModelFactory.StreamingChatToolCallUpdate(0, callId, ChatToolCallKind.Function, name, BinaryData.FromString(piece))
                    : OpenAIChatModelFactory.StreamingChatToolCallUpdate(0, functionArgumentsUpdate: BinaryData.FromString(piece)),
            ]),
        }).ToList();

        updates.AddRange(Call(callId, name, string.Concat(argumentPieces)));
        return updates;
    }

    /// <summary>A content-only update, as a pipeline layer would yield it.</summary>
    public static ChatResponseUpdate Content(AIContent content) =>
        new(ChatRole.Assistant, [content]) { MessageId = "msg-1", ModelId = Model };

    /// <summary>The usage a provider reports for one model call.</summary>
    public static ChatResponseUpdate Usage(long input, long output, string model = Model) =>
        new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output })])
        {
            ModelId = model,
        };

    /// <summary>A run with one user message and, optionally, the client's state.</summary>
    public static RunAgentInput Run(string text, string? stateJson = null, string runId = "run-1") => new()
    {
        ThreadId = "thread-1",
        RunId = runId,
        Messages = [new AGUIUserMessage { Id = "user-1", Content = text }],
        State = stateJson is null ? null : JsonDocument.Parse(stateJson).RootElement.Clone(),
    };

    private static IDictionary<string, object?>? ParseArguments(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(json, AIJsonUtilities.DefaultOptions);
}
