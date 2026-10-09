using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ch02.HandBuilt.Agent.Protocol;

/// <summary>
/// Source-generated serialization for the request and the events: camelCase names (the Web
/// defaults) and no <c>null</c> for optional fields that have no value.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunAgentInput))]
[JsonSerializable(typeof(AgentEvent))]
public sealed partial class AgentJsonContext : JsonSerializerContext;
