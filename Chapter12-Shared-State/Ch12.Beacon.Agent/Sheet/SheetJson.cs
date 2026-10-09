using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Beacon.Domain;

namespace Ch12.Beacon.Agent.Sheet;

/// <summary>
/// The sheet's contract in System.Text.Json terms. The same metadata writes snapshots and reads
/// <c>RunAgentInput.state</c>, so what the server sends and what it accepts can't drift apart. Reading
/// is strict: an unknown or duplicate member, a missing required one, <c>null</c> where the type says
/// no, a number in quotes or an enum as a number all fail.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNameCaseInsensitive = false,
    NumberHandling = JsonNumberHandling.Strict,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(NameOnlyEnumConverter<WorkOrderStatus>), typeof(NameOnlyEnumConverter<WorkOrderPriority>)])]
[JsonSerializable(typeof(SheetState))]
[JsonSerializable(typeof(IReadOnlyList<PatchOperation>))]
[JsonSerializable(typeof(ChecklistItem))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
internal sealed partial class SheetJsonContext : JsonSerializerContext;

/// <summary>Enums travel as their names: <c>"Scheduled"</c> is a status, <c>1</c> isn't.</summary>
internal sealed class NameOnlyEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(namingPolicy: null, allowIntegerValues: false)
    where TEnum : struct, Enum;

/// <summary>One RFC 6902 operation, as the agent's sheet tools return it.</summary>
/// <param name="Op">The operation: <c>add</c>, <c>remove</c>, <c>replace</c>, <c>move</c>, <c>copy</c> or <c>test</c>.</param>
/// <param name="Path">The JSON Pointer (RFC 6901) it applies to.</param>
public sealed record PatchOperation(string Op, string Path)
{
    /// <summary>The value for <c>add</c>, <c>replace</c> and <c>test</c>. Unset means absent; JSON <c>null</c> is a value.</summary>
    public JsonElement? Value { get; init; }

    /// <summary>The source pointer for <c>move</c> and <c>copy</c>.</summary>
    public string? From { get; init; }

    public static PatchOperation Test<T>(string path, T value, JsonTypeInfo<T> type) => new("test", path) { Value = JsonSerializer.SerializeToElement(value, type) };

    public static PatchOperation Replace<T>(string path, T value, JsonTypeInfo<T> type) => new("replace", path) { Value = JsonSerializer.SerializeToElement(value, type) };

    public static PatchOperation Add<T>(string path, T value, JsonTypeInfo<T> type) => new("add", path) { Value = JsonSerializer.SerializeToElement(value, type) };
}

/// <summary>Conversions between the sheet's types and the <see cref="JsonElement"/>s AG-UI carries.</summary>
internal static class SheetJson
{
    public static JsonElement ToElement(SheetState state) =>
        JsonSerializer.SerializeToElement(state, SheetJsonContext.Default.SheetState);

    public static JsonElement ToElement(IReadOnlyList<PatchOperation> patch) =>
        JsonSerializer.SerializeToElement(patch, SheetJsonContext.Default.IReadOnlyListPatchOperation);

    public static string ToJson(SheetState state) =>
        JsonSerializer.Serialize(state, SheetJsonContext.Default.SheetState);
}
