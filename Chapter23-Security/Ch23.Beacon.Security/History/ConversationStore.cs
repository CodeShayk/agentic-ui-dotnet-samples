using System.Collections.Concurrent;

namespace Ch23.Beacon.Security.History;

/// <summary>
/// Where conversations live: Chapter 22's store, keyed by the authenticated user <em>and</em> the thread ID.
/// The thread ID alone names nothing: two users who send the same thread ID get two conversations, and
/// every read, list and delete is scoped to the caller.
/// </summary>
public interface IConversationStore
{
    Conversation GetOrCreate(string userId, string threadId);

    /// <summary>The caller's conversation on this thread, or <see langword="null"/> (another user's looks the same as none).</summary>
    Conversation? Find(string userId, string threadId);

    /// <summary>The caller's conversations, latest first.</summary>
    IReadOnlyList<Conversation> ListFor(string userId);

    /// <summary>Deletes the caller's conversation unless a run holds it. Returns <see langword="false"/> if it is busy.</summary>
    bool TryDelete(Conversation conversation);
}

/// <summary>
/// An in-memory store for the sample. Chapter 22's <c>FileConversationStore</c> takes the same key by hashing
/// the user and the thread together into the folder name; production needs that durability plus a time to
/// live and a cap per user.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<(string UserId, string ThreadId), Conversation> _conversations = new();

    public Conversation GetOrCreate(string userId, string threadId) =>
        _conversations.GetOrAdd((userId, threadId), key => new Conversation(key.UserId, key.ThreadId));

    public Conversation? Find(string userId, string threadId) =>
        _conversations.TryGetValue((userId, threadId), out var conversation) && conversation.LastRunId is not null ? conversation : null;

    public IReadOnlyList<Conversation> ListFor(string userId) =>
        [.. _conversations.Values.Where(c => c.UserId == userId && c.LastRunId is not null).OrderByDescending(c => c.UpdatedAt)];

    public bool TryDelete(Conversation conversation)
    {
        using var lease = conversation.TryBeginRun();
        return lease is not null && _conversations.TryRemove((conversation.UserId, conversation.ThreadId), out _);
    }
}
