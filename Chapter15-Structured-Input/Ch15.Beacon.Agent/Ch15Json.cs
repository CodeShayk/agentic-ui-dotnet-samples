using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.AI;
using Ch15.Beacon.Agent.Input;
using Microsoft.Extensions.AI;

namespace Ch15.Beacon.Agent;

/// <summary>Source-generated serialization for this chapter's tool results and pipeline content.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InputRequestedContent))]
[JsonSerializable(typeof(SerialWarranty))]
[JsonSerializable(typeof(SerialNumberOnRecord))]
[JsonSerializable(typeof(SerialNumberRecorded))]
[JsonSerializable(typeof(FanMotorReading))]
[JsonSerializable(typeof(InputAbandoned))]
public sealed partial class Ch15JsonContext : JsonSerializerContext;

/// <summary>Serializer options for this chapter's tools: compact, with Beacon's types and Microsoft.Extensions.AI's defaults behind ours.</summary>
public static class Ch15Json
{
    /// <summary>The discriminator <see cref="InputRequestedContent"/> is registered under.</summary>
    public const string InputRequestedDiscriminator = "beaconInputRequested";

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        // AIJsonUtilities.DefaultOptions writes indented JSON, and tool results go on the wire as text.
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
        options.TypeInfoResolverChain.Insert(0, Ch15JsonContext.Default);
        options.TypeInfoResolverChain.Insert(1, BeaconJsonContext.Default);
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>What the model reads when a request for input was abandoned.</summary>
public sealed record InputAbandoned(string Status, string Message);
