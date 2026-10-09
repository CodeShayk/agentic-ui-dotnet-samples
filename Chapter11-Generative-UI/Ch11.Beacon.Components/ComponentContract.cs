using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Ch11.Beacon.Components;

/// <summary>
/// One component the agent may show: the tool the model calls, the component a client renders and the
/// schema the call's arguments (the props) must satisfy.
/// </summary>
/// <param name="ToolName">The tool name the model calls, such as <c>show_part_card</c>.</param>
/// <param name="ComponentName">The component the client renders, such as <c>PartCard</c>.</param>
/// <param name="Description">What the model is told about when to use it.</param>
/// <param name="Schema">The JSON Schema of the props. The model sees it as the tool's parameters.</param>
/// <param name="PropsType">Source-generated metadata for the typed props.</param>
public sealed record ComponentContract(
    string ToolName,
    string ComponentName,
    string Description,
    JsonElement Schema,
    JsonTypeInfo PropsType)
{
    /// <summary>Checks props against <see cref="Schema"/>; an empty list means they conform.</summary>
    public IReadOnlyList<string> Validate(JsonElement props) => PropSchemaValidator.Validate(props, Schema);

    internal static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
