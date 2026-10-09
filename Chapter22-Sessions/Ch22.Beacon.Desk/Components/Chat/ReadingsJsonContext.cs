using System.Text.Json.Serialization;

namespace Ch22.Beacon.Desk.Components.Chat;

/// <summary>The readings payload, serialized without reflection.</summary>
[JsonSerializable(typeof(Dictionary<string, decimal>))]
internal sealed partial class ReadingsJsonContext : JsonSerializerContext;
