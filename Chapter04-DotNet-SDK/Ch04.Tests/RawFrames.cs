using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;

namespace Ch04.Tests;

/// <summary>
/// Reads a run's SSE frames as raw JSON, before any AG-UI reader touches them. The SDK's reader treats
/// an explicit <c>null</c> in an optional field as absent, so it would hide the bug a null test looks for.
/// </summary>
internal static class RawFrames
{
    public static async Task<IReadOnlyList<JsonElement>> PostAsync(
        HttpClient http,
        string runAgentInputJson,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = new StringContent(runAgentInputJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        List<JsonElement> frames = [];
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(item.Data);
            frames.Add(document.RootElement.Clone());
        }

        return frames;
    }
}
