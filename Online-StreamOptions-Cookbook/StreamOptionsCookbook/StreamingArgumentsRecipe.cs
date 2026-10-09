using AGUI.Server;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace StreamOptionsCookbook;

/// <summary>
/// Recipe: <see cref="AGUIStreamOptions.MapStreamingToolCallArguments"/> streams a call's arguments as the
/// model writes them. Microsoft.Extensions.AI hands the pipeline a <see cref="FunctionCallContent"/> only when
/// the call is complete; the pieces survive only in the provider's raw updates, so the extractor is provider-specific.
/// </summary>
public static class StreamingArgumentsRecipe
{
    public static AGUIStreamOptions Apply(AGUIStreamOptions options) => options
        .MapStreamingToolCallArguments(FromOpenAI);

    /// <summary>
    /// The pieces of streaming tool calls in one OpenAI (or Azure OpenAI) chunk, or <see langword="null"/>
    /// for any other update. A chunk can carry pieces of several calls; each names its call by index, and
    /// only the first piece of a call carries the call's ID and function name.
    /// </summary>
    public static IEnumerable<AGUIToolCallArgumentFragment>? FromOpenAI(ChatResponseUpdate update) =>
        update.RawRepresentation is StreamingChatCompletionUpdate { ToolCallUpdates.Count: > 0 } chunk
            ? chunk.ToolCallUpdates.Select(piece => new AGUIToolCallArgumentFragment
            {
                Index = piece.Index,
                ToolCallId = piece.ToolCallId,
                FunctionName = piece.FunctionName,
                ArgumentsDelta = piece.FunctionArgumentsUpdate?.ToString() ?? string.Empty,
            })
            : null;
}
