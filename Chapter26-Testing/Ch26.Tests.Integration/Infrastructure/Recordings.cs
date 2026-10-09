using Ch26.Beacon.Agent.Recording;
using Microsoft.Extensions.AI;

namespace Ch26.Tests.Integration.Infrastructure;

/// <summary>
/// Model output recorded by <see cref="ModelRecorder"/> (run the agent with
/// <c>--Beacon:Recording:Directory=...</c>, ask, and copy the files here). The <c>scripted/</c> files come
/// from Beacon's scripted model; the <c>llama3.2/</c> files from llama3.2 on Ollama, on 2026-10-03.
/// </summary>
public static class Recordings
{
    public const string Status = "scripted/wo-1001-status";
    public const string Order = "scripted/order-compressor";
    public const string OrderApproved = "scripted/order-compressor-approved";

    public const string LlamaWarranty1 = "llama3.2/warranty-eq501-1";
    public const string LlamaWarranty2 = "llama3.2/warranty-eq501-2";
    public const string LlamaWarranty3 = "llama3.2/warranty-eq501-3";
    public const string LlamaStatusWrongTool = "llama3.2/wo-1001-status-1";
    public const string LlamaStatus = "llama3.2/wo-1001-status-2";
    public const string LlamaOrderStringQuantity = "llama3.2/order-compressor";

    public static string PathOf(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Recordings", $"{name}.model.json");

    public static Task<ModelRecording> LoadAsync(string name) =>
        ModelRecording.LoadAsync(PathOf(name), TestContext.Current.CancellationToken);

    /// <summary>Every turn of the named recordings, in order: one per model call the replay will answer.</summary>
    public static async Task<IReadOnlyList<ChatResponseUpdate>[]> TurnsAsync(params string[] names)
    {
        List<IReadOnlyList<ChatResponseUpdate>> turns = [];
        foreach (var name in names)
        {
            turns.AddRange((await LoadAsync(name)).Turns);
        }

        return [.. turns];
    }
}
