using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ch11.Beacon.Components;

/// <summary>
/// Source-generated serialization for the props and the result envelope: camelCase like the rest of the
/// wire, unset members omitted, no reflection (the clients run trimmed in .NET MAUI and WebAssembly).
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PartCardProps))]
[JsonSerializable(typeof(ScheduleTimelineProps))]
[JsonSerializable(typeof(FaultHistoryChartProps))]
[JsonSerializable(typeof(ComponentResult))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class ComponentJsonContext : JsonSerializerContext;
