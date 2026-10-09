using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace Ch08.Tests;

/// <summary>
/// A custom <see cref="IAGUITransport"/>: an agent in the same process, no HTTP. It runs the same
/// <c>AGUI.Server</c> conversion an endpoint does, and frames the input and every event as JSON, one
/// event object per frame, the way the specification asks a custom transport that carries JSON to.
/// </summary>
/// <param name="agent">The agent's chat client pipeline.</param>
public sealed class InProcessAgentTransport(IChatClient agent) : IAGUITransport
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    private static readonly JsonTypeInfo<RunAgentInput> InputInfo = (JsonTypeInfo<RunAgentInput>)Json.GetTypeInfo(typeof(RunAgentInput));
    private static readonly JsonTypeInfo<BaseEvent> EventInfo = (JsonTypeInfo<BaseEvent>)Json.GetTypeInfo(typeof(BaseEvent));

    /// <summary>Every input the transport delivered, as the agent received it.</summary>
    public List<RunAgentInput> Inputs { get; } = [];

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The input crosses the boundary as JSON, so client and agent share no objects.
        var received = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(input, InputInfo), InputInfo)!;
        Inputs.Add(received);

        var context = received.ToChatRequestContext(Json);
        var updates = agent.GetStreamingResponseAsync(context.Messages, context.ChatOptions, cancellationToken);

        await foreach (var evt in updates.AsAGUIEventStreamAsync(context, cancellationToken).ConfigureAwait(false))
        {
            // One event, one frame, in order: the binding contract every transport must keep.
            var frame = JsonSerializer.SerializeToUtf8Bytes(evt, EventInfo);
            yield return JsonSerializer.Deserialize(frame, EventInfo)!;
        }
    }

    // The same JSON configuration Beacon.AGUI.Hosting gives an HTTP endpoint.
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver);
        options.TypeInfoResolverChain.Add(AIJsonUtilities.DefaultOptions.TypeInfoResolver!);
        AGUIJsonUtilities.RegisterInterruptContentTypes(options);
        options.MakeReadOnly();
        return options;
    }
}
