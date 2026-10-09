using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch02.Tests;

/// <summary>The plumbing every test model shares; each one only decides what it streams.</summary>
internal abstract class TestChatClient : IChatClient
{
    public abstract IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    protected static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);
}

/// <summary>Streams a fixed answer and records the messages of every request.</summary>
internal sealed class RecordingChatClient(params string[] chunks) : TestChatClient
{
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add([.. messages]);
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return Text(chunk);
        }
    }
}

/// <summary>Streams some text, then fails the way a dropped provider connection does.</summary>
internal sealed class FailingChatClient(params string[] chunksBeforeFailure) : TestChatClient
{
    public const string SecretDetail = "model-gateway.internal:443";

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in chunksBeforeFailure)
        {
            await Task.Yield();
            yield return Text(chunk);
        }

        throw new HttpRequestException($"Connection reset by {SecretDetail}.");
    }
}

/// <summary>Streams one chunk, then waits for the rest forever, noting when it is cancelled.</summary>
internal sealed class StallingChatClient : TestChatClient
{
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return Text("Let me check ");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Cancelled.TrySetResult();
            throw;
        }

        yield return Text("the capacitor.");
    }
}
