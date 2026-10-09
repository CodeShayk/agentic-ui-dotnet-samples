using System.Text.Json;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook.Tests.Infrastructure;

/// <summary>Reading raw wire frames, and saving them for the cookbook's wire listings.</summary>
internal static class Frames
{
    public static string Type(this JsonElement frame) => frame.GetProperty("type").GetString()!;

    public static string? Str(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static IReadOnlyList<string> Types(this IEnumerable<JsonElement> frames) => [.. frames.Select(Type)];

    public static JsonElement Only(this IEnumerable<JsonElement> frames, string type) => Assert.Single(frames, f => f.Type() == type);

    public static int IndexOf(this IReadOnlyList<JsonElement> frames, string type)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].Type() == type)
            {
                return i;
            }
        }

        return -1;
    }

    public static bool HasRawEvent(this JsonElement frame) => frame.TryGetProperty("rawEvent", out _);

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static bool SameJson(this JsonElement expected, JsonElement actual) => JsonElement.DeepEquals(expected, actual);

    /// <summary>
    /// When <c>STREAM_OPTIONS_CAPTURE_DIR</c> is set, writes the frames as the SSE body the server sent:
    /// one <c>data:</c> line per frame, each payload byte for byte as it arrived.
    /// </summary>
    public static void Capture(this IReadOnlyList<JsonElement> frames, string name)
    {
        var directory = Environment.GetEnvironmentVariable("STREAM_OPTIONS_CAPTURE_DIR");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, name + ".sse"),
            string.Concat(frames.Select(frame => $"data: {frame.GetRawText()}\n\n")));
    }
}

/// <summary>Builds the server pipelines the tests run: tools installed, function invocation, the model at the bottom.</summary>
internal static class Pipelines
{
    public static IChatClient WithTools(IChatClient model, params AITool[] tools) =>
        new ChatClientBuilder(model)
            .ConfigureOptions(options => options.Tools = [.. options.Tools ?? [], .. tools])
            .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
            .Build();
}

/// <summary>Answers <see cref="ChatClientMetadata"/> the way a real provider's client does.</summary>
internal sealed class ProviderLabeledChatClient(IChatClient innerClient, string provider) : DelegatingChatClient(innerClient)
{
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) && serviceKey is null
            ? new ChatClientMetadata(provider, defaultModelId: Turns.Model)
            : base.GetService(serviceType, serviceKey);
}
