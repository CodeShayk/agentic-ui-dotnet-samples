using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Ch05.Tests;

/// <summary>
/// A stand-in for an Ollama server on a random local port. It records every <c>/api/chat</c>
/// request body (what the model provider was actually sent) and answers with NDJSON recorded from
/// a real llama3.2 run. Pointing the agent at it with <c>AI:Provider=Ollama</c> swaps the provider
/// purely by configuration, and the real OllamaSharp client does the talking.
/// </summary>
public sealed class FakeOllama : IAsyncDisposable
{
    private static readonly string RecordingsPath = Path.Combine(AppContext.BaseDirectory, "Recordings");

    /// <summary>llama3.2 calling <c>get_work_order</c> for WO-1001.</summary>
    public static string ToolCallRecording { get; } = File.ReadAllText(Path.Combine(RecordingsPath, "llama3.2-tool-call.ndjson"));

    /// <summary>llama3.2 answering from the tool result.</summary>
    public static string AnswerRecording { get; } = File.ReadAllText(Path.Combine(RecordingsPath, "llama3.2-answer.ndjson"));

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<JsonElement> _requests = new();

    private FakeOllama(WebApplication app) => _app = app;

    /// <summary>The base address to configure as <c>AI:OllamaEndpoint</c>.</summary>
    public Uri Endpoint => new(_app.Urls.First());

    /// <summary>Every chat request the provider received, in order.</summary>
    public IReadOnlyList<JsonElement> Requests => [.. _requests];

    /// <summary>
    /// Starts the fake. The default reply mimics the recorded run: call the tool when a work order is
    /// mentioned and the tool is offered, otherwise answer with text.
    /// </summary>
    public static async Task<FakeOllama> StartAsync(
        Func<JsonElement, string>? reply = null,
        CancellationToken cancellationToken = default)
    {
        reply ??= DefaultReply;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeOllama(app);

        app.MapPost("/api/chat", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var request = body.RootElement.Clone();
            fake._requests.Enqueue(request);

            context.Response.ContentType = "application/x-ndjson";
            await context.Response.WriteAsync(reply(request), context.RequestAborted);
        });

        await app.StartAsync(cancellationToken);
        return fake;
    }

    public static string DefaultReply(JsonElement request)
    {
        var messages = request.GetProperty("messages").EnumerateArray().ToList();
        var last = messages[^1];
        var offersTool = request.TryGetProperty("tools", out var tools)
            && tools.EnumerateArray().Any(t => t.GetProperty("function").GetProperty("name").GetString() == "get_work_order");

        return last.GetProperty("role").GetString() == "user"
            && last.GetProperty("content").GetString()!.Contains("WO-1001", StringComparison.Ordinal)
            && offersTool
                ? ToolCallRecording
                : AnswerRecording;
    }

    /// <summary>The <c>role</c> and <c>content</c> of each message in a recorded request.</summary>
    public static IReadOnlyList<(string Role, string Content)> Messages(JsonElement request) =>
        [.. request.GetProperty("messages").EnumerateArray().Select(m =>
            (m.GetProperty("role").GetString()!, m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : ""))];

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
