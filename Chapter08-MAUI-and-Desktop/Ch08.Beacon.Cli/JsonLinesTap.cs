using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AGUI.Abstractions;
using AGUI.Client;

namespace Ch08.Beacon.Cli;

/// <summary>
/// Writes every event of every run to a <see cref="TextWriter"/> as one line of JSON, for operators who
/// pipe Beacon into <c>jq</c> or a log. It sits under the retry layer, so it shows exactly what arrived.
/// </summary>
public sealed class JsonLinesTap(IAGUITransport inner, TextWriter output) : IAGUITransport
{
    private static readonly JsonTypeInfo<BaseEvent> EventInfo = (JsonTypeInfo<BaseEvent>)new JsonSerializerOptions
    {
        // The AG-UI resolver omits unset fields, as the wire does.
        TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver,
    }.GetTypeInfo(typeof(BaseEvent));

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in inner.SendAsync(input, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(evt, EventInfo).AsMemory(), cancellationToken).ConfigureAwait(false);
            yield return evt;
        }
    }
}
