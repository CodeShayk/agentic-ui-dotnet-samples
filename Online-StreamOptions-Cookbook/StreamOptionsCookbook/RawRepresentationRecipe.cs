using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook;

/// <summary>
/// Shows one progress activity per run: an <c>ACTIVITY_SNAPSHOT</c> before the inner client's answer and an
/// <c>ACTIVITY_DELTA</c> after it. No <see cref="AIContent"/> maps to activity events, so the layer builds the
/// events itself and hands each to <c>AGUI.Server</c> in <see cref="ChatResponseUpdate.RawRepresentation"/>.
/// </summary>
public sealed class ProgressActivityChatClient(IChatClient innerClient, string label) : DelegatingChatClient(innerClient)
{
    public const string ActivityType = "beacon.progress";

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // One activity message per run, named after the run.
        var runId = options?.TryGetRunAgentInput(out var input) == true ? input.RunId : Guid.NewGuid().ToString("N");
        var messageId = $"activity-{runId}";

        yield return new ChatResponseUpdate
        {
            RawRepresentation = new ActivitySnapshotEvent
            {
                MessageId = messageId,
                ActivityType = ActivityType,
                Content = CookbookJson.ToElement(new JsonObject { ["label"] = label, ["status"] = "running" }),
            },
        };

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }

        yield return new ChatResponseUpdate
        {
            RawRepresentation = new ActivityDeltaEvent
            {
                MessageId = messageId,
                ActivityType = ActivityType,
                Patch = CookbookJson.ToElement(new JsonArray(
                    new JsonObject { ["op"] = "replace", ["path"] = "/status", ["value"] = "done" })),
            },
        };
    }
}

/// <summary>Recipe: a <see cref="BaseEvent"/> in <see cref="ChatResponseUpdate.RawRepresentation"/> goes on the wire verbatim.</summary>
public static class RawRepresentationRecipe
{
    /// <summary>Adds <see cref="ProgressActivityChatClient"/> to a pipeline. Put it above function invocation to bracket the whole run.</summary>
    public static ChatClientBuilder UseProgressActivity(this ChatClientBuilder builder, string label) =>
        builder.Use(inner => new ProgressActivityChatClient(inner, label));
}
