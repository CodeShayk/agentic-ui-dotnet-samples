using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ch13.Beacon.Agent;

/// <summary>Turns a <see cref="JsonNode"/> into the <see cref="JsonElement"/> AG-UI events carry, without reflection.</summary>
internal static class JsonNodes
{
    public static JsonElement ToElement(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            node.WriteTo(writer);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
