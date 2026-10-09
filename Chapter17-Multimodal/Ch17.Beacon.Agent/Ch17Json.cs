using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.AI;
using Ch17.Beacon.Agent.Media;
using Microsoft.Extensions.AI;

namespace Ch17.Beacon.Agent;

/// <summary>Source-generated serialization for this chapter's tool results and events.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MediaRefusalReport))]
[JsonSerializable(typeof(SerialNumberRecorded))]
public sealed partial class Ch17JsonContext : JsonSerializerContext;

/// <summary>Serializer options for this chapter's tools: compact, with Beacon's types and Microsoft.Extensions.AI's defaults behind ours.</summary>
public static class Ch17Json
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        // AIJsonUtilities.DefaultOptions writes indented JSON, and tool results go on the wire as text.
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
        options.TypeInfoResolverChain.Insert(0, Ch17JsonContext.Default);
        options.TypeInfoResolverChain.Insert(1, BeaconJsonContext.Default);
        options.MakeReadOnly();
        return options;
    }
}
