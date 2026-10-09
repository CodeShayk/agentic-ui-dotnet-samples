using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Ch26.Beacon.Agent.Recording;

/// <summary>
/// What a model said during one AG-UI run: one list of <see cref="ChatResponseUpdate"/> per model call
/// ("turn"), in order. Stored as indented JSON, written by <see cref="ModelRecorder"/> and replayed in
/// tests through <c>RecordedChatClient</c>, which answers each call with the next turn.
/// </summary>
public sealed record ModelRecording(string? Model, IReadOnlyList<IReadOnlyList<ChatResponseUpdate>> Turns)
{
    public const string Format = "beacon-model-recording/1";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The updates of one model call as JSON, captured before any layer above can change them.</summary>
    public static JsonNode Capture(ChatResponseUpdate update) =>
        JsonSerializer.SerializeToNode(update, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ChatResponseUpdate)))!;

    /// <summary>Adds one turn to the recording at <paramref name="path"/>, creating the file for a run's first turn.</summary>
    public static async Task AppendTurnAsync(string path, string? model, IReadOnlyList<JsonNode> turn, CancellationToken cancellationToken = default)
    {
        var root = File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))!.AsObject()
            : new JsonObject { ["format"] = Format, ["model"] = model, ["turns"] = new JsonArray() };

        root["turns"]!.AsArray().Add(new JsonArray([.. turn.Select(u => u.DeepClone())]));
        await File.WriteAllTextAsync(path, root.ToJsonString(Indented), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a recording back as updates, ready for <c>RecordedChatClient</c>.</summary>
    public static async Task<ModelRecording> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var file = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (!root.TryGetProperty("format", out var format) || format.GetString() != Format)
        {
            throw new InvalidDataException($"{path} is not a {Format} file.");
        }

        var typeInfo = AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ChatResponseUpdate));
        List<IReadOnlyList<ChatResponseUpdate>> turns = [];
        foreach (var turn in root.GetProperty("turns").EnumerateArray())
        {
            turns.Add([.. turn.EnumerateArray().Select(u => (ChatResponseUpdate)u.Deserialize(typeInfo)!)]);
        }

        return new ModelRecording(root.TryGetProperty("model", out var model) ? model.GetString() : null, turns);
    }

    /// <summary>The turns, as <c>RecordedChatClient</c>'s constructor takes them.</summary>
    public IReadOnlyList<ChatResponseUpdate>[] ToTurns() => [.. Turns];
}
