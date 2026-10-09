using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch02.Tests;

/// <summary>Hosts the hand-built agent in memory, with a test model in place of the configured one.</summary>
internal sealed class TestAgent(IChatClient model) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services => services.AddSingleton(model));
}

/// <summary>Posts runs and reads the SSE stream back as raw wire JSON.</summary>
internal static class Wire
{
    /// <summary>A minimal run with one user message, written the way it travels.</summary>
    public static string UserRun(string text, string threadId = "thread-1", string runId = "run-1") =>
        $$"""
        {"threadId":"{{threadId}}","runId":"{{runId}}","protocolVersion":"1.0","messages":[{"id":"user-1","role":"user","content":"{{text}}"}]}
        """;

    public static Task<HttpResponseMessage> PostAsync(HttpClient http, string json, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>Runs the agent and returns every event's JSON, in order.</summary>
    public static async Task<IReadOnlyList<JsonElement>> RunAsync(HttpClient http, string json, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(http, json, cancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var events = new List<JsonElement>();
        await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(item.Data);
            events.Add(document.RootElement.Clone());
        }

        return events;
    }

    public static string[] Types(IEnumerable<JsonElement> events) =>
        [.. events.Select(e => e.GetProperty("type").GetString()!)];

    public static string Text(IEnumerable<JsonElement> events) =>
        string.Concat(events
            .Where(e => e.GetProperty("type").GetString() == "TEXT_MESSAGE_CONTENT")
            .Select(e => e.GetProperty("delta").GetString()));
}
