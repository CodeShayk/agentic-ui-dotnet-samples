using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using AGUI.Abstractions;

namespace Beacon.Client.Core;

/// <summary>Conversions between the JSON shapes the SDK and the view use. All of them are trim- and AOT-safe.</summary>
internal static class JsonValues
{
    private static readonly JsonTypeInfo ArgumentsTypeInfo = AGUIJsonSerializerContext.Default.IDictionaryStringObject;

    /// <summary>Parses argument text if it is a complete JSON object; partial or invalid text gives <see langword="null"/>.</summary>
    public static JsonElement? TryParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Microsoft.Extensions.AI's argument dictionary as a JSON object.</summary>
    public static JsonElement? FromArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.SerializeToElement(arguments, ArgumentsTypeInfo);
        }
        catch (NotSupportedException)
        {
            return null; // a value type the AG-UI serializer context doesn't know
        }
    }

    /// <summary>A JSON object as Microsoft.Extensions.AI's argument dictionary (values are <see cref="JsonElement"/>s).</summary>
    public static IDictionary<string, object?>? ToArguments(JsonElement? arguments) =>
        arguments is { ValueKind: JsonValueKind.Object } element
            ? (IDictionary<string, object?>?)element.Deserialize(ArgumentsTypeInfo)
            : null;

    /// <summary>Parses any JSON text; <see langword="null"/> when it isn't JSON (or is JSON <c>null</c>).</summary>
    public static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A <see cref="JsonElement"/> as a mutable node tree the reducer copies before patching.</summary>
    public static JsonNode? ToNode(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());
}
