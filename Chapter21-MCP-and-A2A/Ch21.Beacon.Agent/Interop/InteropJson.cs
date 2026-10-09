using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch21.Beacon.Agent.Interop;

/// <summary>Source-generated serialization for what Beacon sends to, and keeps from, other systems.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExternalContentResult))]
[JsonSerializable(typeof(SupplierAvailability))]
[JsonSerializable(typeof(SupplierStockRequest))]
[JsonSerializable(typeof(SupplierQuote))]
[JsonSerializable(typeof(ToolError))]
[JsonSerializable(typeof(RefusedRequest))]
public sealed partial class InteropJsonContext : JsonSerializerContext;

/// <summary>Serializer options for the interop tools: their types first, then Microsoft.Extensions.AI's defaults.</summary>
public static class InteropJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
        options.TypeInfoResolverChain.Insert(0, InteropJsonContext.Default);
        options.MakeReadOnly();
        return options;
    }
}
