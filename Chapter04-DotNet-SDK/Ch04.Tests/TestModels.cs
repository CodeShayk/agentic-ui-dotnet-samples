using System.Runtime.CompilerServices;
using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch04.Tests;

internal static class TestModels
{
    /// <summary>Beacon's scripted model without the streaming delay.</summary>
    public static ScriptedChatClient Scripted() =>
        new(options: new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero });
}

/// <summary>A model that starts answering and then fails, the way a provider outage looks mid-stream.</summary>
internal sealed class FailingChatClient : IChatClient
{
    public const string SecretDetail = "key ending 7f3a";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Checking WO-1001") { MessageId = "msg-1" };
        throw new HttpRequestException($"503 from https://contoso-prod.openai.azure.com ({SecretDetail})");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// A model that can wait before its first update and pause between its two updates. The pauses are
/// settable between requests, so a test can warm the host up first.
/// </summary>
internal sealed class PacedChatClient : IChatClient
{
    public TimeSpan BeforeFirstUpdate { get; set; }

    public TimeSpan BetweenUpdates { get; set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(BeforeFirstUpdate, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Checking ") { MessageId = "msg-1" };
        await Task.Delay(BetweenUpdates, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "WO-1001.") { MessageId = "msg-1" };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>An HTTP handler that answers every request with the same Server-Sent Events body.</summary>
internal sealed class CannedSseHandler(params string[] frames) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = string.Concat(frames.Select(frame => $"data: {frame}\n\n"));
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "text/event-stream"),
        });
    }
}
