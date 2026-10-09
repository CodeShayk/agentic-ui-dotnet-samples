using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;

namespace Ch22.Tests.Infrastructure;

/// <summary>Raw HTTP for runs: the request as a client writes it, the frames exactly as the server wrote them.</summary>
public static class Wire
{
    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, RunAgentInput input, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = new StringContent(JsonSerializer.Serialize(input, AGUIJsonSerializerContext.Default.RunAgentInput), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>Each SSE <c>data:</c> payload, parsed but never re-serialized, so a written <c>null</c> stays visible.</summary>
    public static async Task<IReadOnlyList<JsonElement>> FramesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        List<JsonElement> frames = [];
        await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(item.Data);
            frames.Add(document.RootElement.Clone());
        }

        return frames;
    }

    public static async Task<IReadOnlyList<JsonElement>> RunAsync(HttpClient http, RunAgentInput input, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(http, input, cancellationToken);
        return await FramesAsync(response, cancellationToken);
    }

    public static async Task<IReadOnlyList<JsonElement>> GetAsync(HttpClient http, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return await FramesAsync(response, cancellationToken);
    }

    public static IReadOnlyList<string> Types(IEnumerable<JsonElement> frames) => [.. frames.Select(f => f.GetProperty("type").GetString()!)];

    public static AGUIUserMessage User(string id, string text) => new() { Id = id, Content = text };
}
