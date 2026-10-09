using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ch26.Tests.Integration.Infrastructure;

/// <summary>
/// The wire as a client sees it: a <c>RunAgentInput</c> posted as JSON text, and each SSE <c>data:</c>
/// payload parsed with <see cref="JsonDocument"/> but never re-serialized through the SDK, which would
/// hide anything the SDK's reader forgives (an explicit <c>null</c>, an unknown property).
/// </summary>
public static class Wire
{
    public static async Task<IReadOnlyList<JsonElement>> PostAsync(HttpClient http, string inputJson, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(http, inputJson, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadFramesAsync(response, cancellationToken);
    }

    public static async Task<IReadOnlyList<JsonElement>> ReadFramesAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        List<JsonElement> frames = [];
        await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(item.Data);
            frames.Add(document.RootElement.Clone());
        }

        return frames;
    }

    public static Task<HttpResponseMessage> SendAsync(HttpClient http, string inputJson, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = new StringContent(inputJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>A run with one user message, as JSON.</summary>
    public static string UserRun(string threadId, string runId, string text) => new JsonObject
    {
        ["threadId"] = threadId,
        ["runId"] = runId,
        ["messages"] = new JsonArray(new JsonObject { ["id"] = "user-1", ["role"] = "user", ["content"] = text }),
    }.ToJsonString();

    public static string TypeOf(JsonElement frame) => frame.GetProperty("type").GetString()!;

    public static IReadOnlyList<string> Types(IEnumerable<JsonElement> frames) => [.. frames.Select(TypeOf)];

    public static IEnumerable<JsonElement> OfType(this IEnumerable<JsonElement> frames, string type) =>
        frames.Where(f => TypeOf(f) == type);

    /// <summary>The text of every <c>TEXT_MESSAGE_CONTENT</c>, joined.</summary>
    public static string Text(IEnumerable<JsonElement> frames) =>
        string.Concat(frames.OfType("TEXT_MESSAGE_CONTENT").Select(f => f.GetProperty("delta").GetString()));
}
