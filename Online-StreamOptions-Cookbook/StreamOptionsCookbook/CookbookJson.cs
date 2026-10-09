using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook;

/// <summary>The JSON plumbing the recipes share.</summary>
public static class CookbookJson
{
    /// <summary>
    /// Options for the recipes' tools. <see cref="AIJsonUtilities.DefaultOptions"/> writes indented JSON, and
    /// <see cref="AIFunctionFactory"/> serializes every return value with the options it is given, so a tool
    /// built with the defaults puts line breaks into every <c>TOOL_CALL_RESULT</c>. These are compact.
    /// </summary>
    public static JsonSerializerOptions ToolOptions { get; } = CreateToolOptions();

    /// <summary>
    /// Registers the recipes' own <see cref="AIContent"/> types with an endpoint's JSON options.
    /// <c>AGUI.Server</c> serializes every update it converts (for <c>rawEvent</c>) with those options,
    /// so content they can't serialize ends the run in <c>RUN_ERROR</c> before any mapper sees it.
    /// </summary>
    public static void RegisterContentTypes(JsonSerializerOptions options)
    {
        options.TypeInfoResolverChain.Insert(1, CookbookJsonContext.Default);
        options.AddAIContentType<PhaseContent>(MapContentRecipe.PhaseDiscriminator);
        options.AddAIContentType<ConfirmationRequestContent>(MapInterruptRecipe.ConfirmationDiscriminator);
    }

    /// <summary>A <see cref="JsonNode"/> as the <see cref="JsonElement"/> that AG-UI events carry.</summary>
    public static JsonElement ToElement(JsonNode node) =>
        JsonSerializer.SerializeToElement(node, CookbookJsonContext.Default.JsonNode);

    /// <summary>A tool-call argument as JSON, whether the provider parsed it into a <see cref="JsonElement"/> or not.</summary>
    public static JsonNode? Argument(FunctionCallContent call, string name) =>
        call.Arguments?.TryGetValue(name, out var value) == true && value is not null
            ? JsonSerializer.SerializeToNode(value, ToolOptions.GetTypeInfo(value.GetType()))
            : null;

    /// <summary>The client's state for the run the current tool call belongs to, or an empty object.</summary>
    public static JsonElement CurrentRunState() =>
        FunctionInvokingChatClient.CurrentContext?.Options?.TryGetRunAgentInput(out var input) == true
            && input.State is { ValueKind: JsonValueKind.Object } state
                ? state
                : EmptyObject;

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonSerializerOptions CreateToolOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
        options.TypeInfoResolverChain.Insert(0, CookbookJsonContext.Default);
        options.MakeReadOnly();
        return options;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PhaseContent))]
[JsonSerializable(typeof(ConfirmationRequestContent))]
[JsonSerializable(typeof(PartOrderPlaced))]
[JsonSerializable(typeof(WarrantyCheck))]
[JsonSerializable(typeof(WorkSheet))]
[JsonSerializable(typeof(JsonNode))]
internal sealed partial class CookbookJsonContext : JsonSerializerContext;
