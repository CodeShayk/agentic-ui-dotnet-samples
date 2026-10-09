// Adapted from Chapter 22 (Ch22.Beacon.Agent).
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AGUI.Abstractions;

namespace Ch27.Beacon.Agent.Conversations;

/// <summary>Source-generated metadata for what the store writes.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RunRecord))]
[JsonSerializable(typeof(ConversationHeader))]
[JsonSerializable(typeof(StoredEvent))]
public sealed partial class ConversationJsonContext : JsonSerializerContext;

/// <summary>
/// The options for everything the store persists. AG-UI types (messages, events) resolve through the SDK's
/// resolver, so a stored message has exactly the shape that travels in <c>MESSAGES_SNAPSHOT</c>; the store's own
/// records resolve through <see cref="ConversationJsonContext"/>. Nothing is written as null.
/// </summary>
public static class ConversationJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    /// <summary>An AG-UI event as JSON, without <c>rawEvent</c>: the SDK's echo of the provider's update is not conversation.</summary>
    public static JsonElement ToStoredJson(BaseEvent evt)
    {
        var node = JsonSerializer.SerializeToNode(evt, AGUIJsonSerializerContext.Default.BaseEvent)!.AsObject();
        node.Remove("rawEvent");
        return JsonSerializer.SerializeToElement(node, AGUIJsonSerializerContext.Default.JsonObject);
    }

    public static BaseEvent? ToEvent(JsonElement stored) =>
        stored.Deserialize(AGUIJsonSerializerContext.Default.BaseEvent);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(AGUIJsonUtilities.DefaultTypeInfoResolver, ConversationJsonContext.Default),
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.MakeReadOnly();
        return options;
    }
}
