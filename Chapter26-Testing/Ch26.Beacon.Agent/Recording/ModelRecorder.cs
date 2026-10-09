using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace Ch26.Beacon.Agent.Recording;

/// <summary>
/// Saves what the model says in each AG-UI run, one file per run named after its run ID, so a real
/// model's answer can be replayed in tests (section 26.2). It wraps the model itself, beneath every other
/// layer, and copies each update as it arrives, before the layers above can change it. Only completed
/// model calls are saved: a cancelled or failed call isn't an answer to replay.
/// </summary>
/// <remarks>Recordings hold whole conversations. The agent enables this only in Development.</remarks>
public sealed class ModelRecorder(IChatClient innerClient, string directory, ILogger<ModelRecorder> logger)
    : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<JsonNode> turn = [];
        string? model = null;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            turn.Add(ModelRecording.Capture(update));
            model ??= update.ModelId;
            yield return update;
        }

        var runId = options is not null && options.TryGetRunAgentInput(out var input) ? input.RunId : "no-run";
        var path = Path.Combine(directory, $"{FileNameFor(runId)}.model.json");
        await ModelRecording.AppendTurnAsync(path, model, turn, CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Recorded a model turn of {Count} updates to {Path}.", turn.Count, path);
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    /// <summary>Wraps <paramref name="model"/> when <c>Beacon:Recording:Directory</c> is set in Development.</summary>
    public static IChatClient WrapIfEnabled(IChatClient model, IServiceProvider services)
    {
        var directory = services.GetRequiredService<IConfiguration>()["Beacon:Recording:Directory"];
        if (string.IsNullOrWhiteSpace(directory) || !services.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            return model;
        }

        Directory.CreateDirectory(directory);
        return new ModelRecorder(model, directory, services.GetRequiredService<ILogger<ModelRecorder>>());
    }

    // Run IDs come from the client; never let one choose where a file goes.
    private static string FileNameFor(string runId) =>
        string.Concat(runId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
