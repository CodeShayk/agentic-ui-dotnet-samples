// Copied from Chapter 23 (Ch23.Beacon.Security). Chapter 27 makes the store asynchronous and durable: the
// in-memory store is replaced by Chapter 22's file store (Conversations/FileConversationStore.cs), which writes
// each run through an IRunJournal.
using AGUI.Abstractions;

namespace Ch27.Beacon.Agent.Security.History;

/// <summary>
/// Where conversations live: Chapter 22's store, keyed by the authenticated user <em>and</em> the thread ID.
/// The thread ID alone names nothing: two users who send the same thread ID get two conversations, and
/// every read, list and delete is scoped to the caller.
/// </summary>
public interface IConversationStore
{
    ValueTask<Conversation> GetOrCreateAsync(string userId, string threadId, CancellationToken cancellationToken);

    /// <summary>The caller's conversation on this thread, or <see langword="null"/> (another user's looks the same as none).</summary>
    ValueTask<Conversation?> FindAsync(string userId, string threadId, CancellationToken cancellationToken);

    /// <summary>The caller's conversations, latest first.</summary>
    ValueTask<IReadOnlyList<Conversation>> ListForAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Deletes the caller's conversation unless a run holds it. Returns <see langword="false"/> if it is busy.</summary>
    ValueTask<bool> TryDeleteAsync(Conversation conversation, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a run as open, with the messages it accepted, and returns the journal its events go to. Call under the
    /// conversation's <see cref="RunLease"/>. A run still open when the process next loads the thread broke under it.
    /// </summary>
    ValueTask<IRunJournal> BeginRunAsync(Conversation conversation, string runId, IReadOnlyList<AGUIMessage> accepted, CancellationToken cancellationToken);
}

/// <summary>One run on its way to disk: every event as it goes out, then the run's record when it ends.</summary>
public interface IRunJournal : IAsyncDisposable
{
    ValueTask AppendAsync(BaseEvent evt, CancellationToken cancellationToken);

    /// <summary>Records the run with how it ended and what it produced, and makes it the thread's latest.</summary>
    ValueTask CommitAsync(RunEnding ending, (string Message, string? Code)? error, IReadOnlyList<AGUIMessage> output, CancellationToken cancellationToken);
}
