using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook;

/// <summary>
/// Marks the start or the end of a named phase of work. A pipeline layer can yield it without
/// referencing AG-UI at all; the endpoint decides what it looks like on the wire.
/// </summary>
public sealed class PhaseContent : AIContent
{
    public required string Name { get; init; }

    public required bool Finished { get; init; }
}

/// <summary>Brackets everything the inner client streams with a pair of <see cref="PhaseContent"/> markers.</summary>
public sealed class PhaseMarkingChatClient(IChatClient innerClient, string phase) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new PhaseContent { Name = phase, Finished = false }]);

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new PhaseContent { Name = phase, Finished = true }]);
    }
}

/// <summary>Recipe: <see cref="AGUIStreamOptions.MapContent"/> turns the pipeline's own content into AG-UI events.</summary>
public static class MapContentRecipe
{
    /// <summary>The <c>$type</c> discriminator <see cref="PhaseContent"/> serializes with.</summary>
    public const string PhaseDiscriminator = "beaconPhase";

    public static AGUIStreamOptions Apply(AGUIStreamOptions options) => options
        .MapContent(content => content switch
        {
            PhaseContent { Finished: false } phase => [new StepStartedEvent { StepName = phase.Name }],
            PhaseContent phase => [new StepFinishedEvent { StepName = phase.Name }],
            _ => null, // not ours: let the next mapper try, or drop it
        });
}
