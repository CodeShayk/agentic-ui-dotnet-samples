using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Beacon.Testing;

/// <summary>
/// An <see cref="IChatClient"/> that replays recorded <see cref="ChatResponseUpdate"/> sequences, one per call,
/// and records what it was asked. Use it to test an AG-UI server without a model.
/// </summary>
public sealed class RecordedChatClient(params IReadOnlyList<ChatResponseUpdate>[] turns) : IChatClient
{
    private readonly Queue<IReadOnlyList<ChatResponseUpdate>> _turns = new(turns);

    /// <summary>The messages and options of every call, in order.</summary>
    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add(([.. messages], options));

        if (!_turns.TryDequeue(out var updates))
        {
            throw new InvalidOperationException($"No recorded turn left for call {Calls.Count}.");
        }

        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    /// <summary>Builds a text-only turn streamed in the given chunks.</summary>
    public static IReadOnlyList<ChatResponseUpdate> TextTurn(string messageId, params string[] chunks) =>
        [.. chunks.Select(c => new ChatResponseUpdate(ChatRole.Assistant, c) { MessageId = messageId })];
}
