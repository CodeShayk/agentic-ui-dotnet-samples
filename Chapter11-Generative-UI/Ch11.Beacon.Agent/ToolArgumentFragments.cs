// Copied from Chapter 9 (Ch09.Beacon.Agent); each chapter's code is a self-contained checkpoint.
using AGUI.Server;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Ch11.Beacon.Agent;

/// <summary>
/// The extractor Beacon registers with <see cref="AGUIStreamOptions.MapStreamingToolCallArguments"/>:
/// it finds the provider-native pieces of a streaming tool call in an update's raw representation, so
/// AGUI.Server can send each piece as its own <c>TOOL_CALL_ARGS</c> event.
/// </summary>
/// <remarks>
/// Reading raw updates is provider-specific, which is why AGUI.Server leaves it to the endpoint. Beacon
/// knows two shapes: the scripted model's (through <see cref="ScriptedArgumentStreaming"/>) and the
/// OpenAI SDK's, which the OpenAI and Azure OpenAI providers use. Ollama sends each call whole, so its
/// updates carry no pieces and its calls keep the single <c>TOOL_CALL_ARGS</c> event.
/// </remarks>
public static class ToolArgumentFragments
{
    public static IEnumerable<AGUIToolCallArgumentFragment>? Extract(ChatResponseUpdate update) =>
        update.RawRepresentation switch
        {
            ScriptedArgumentFragment piece =>
            [
                new AGUIToolCallArgumentFragment
                {
                    Index = piece.Index,
                    ToolCallId = piece.ToolCallId,
                    FunctionName = piece.FunctionName,
                    ArgumentsDelta = piece.Delta,
                },
            ],
            StreamingChatCompletionUpdate { ToolCallUpdates.Count: > 0 } chunk => FromOpenAI(chunk),
            _ => null,
        };

    // An OpenAI chunk can carry pieces of several calls; each names its call by index, and only the
    // first piece of a call has its ID and function name.
    private static IEnumerable<AGUIToolCallArgumentFragment> FromOpenAI(StreamingChatCompletionUpdate chunk) =>
        chunk.ToolCallUpdates.Select(piece => new AGUIToolCallArgumentFragment
        {
            Index = piece.Index,
            ToolCallId = piece.ToolCallId,
            FunctionName = piece.FunctionName,
            ArgumentsDelta = piece.FunctionArgumentsUpdate?.ToString() ?? string.Empty,
        });
}
