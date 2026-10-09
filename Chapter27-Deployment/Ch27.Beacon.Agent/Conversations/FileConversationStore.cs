// Adapted from Chapter 22 (Ch22.Beacon.Agent): the same files and write order, holding Chapter 23's linear,
// per-user transcript. The folder name hashes the user and the thread, so a thread ID names nothing on its own.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Ch27.Beacon.Agent.Security.History;
using Microsoft.Extensions.Options;

namespace Ch27.Beacon.Agent.Conversations;

/// <summary>Where Beacon keeps conversations, and for how long.</summary>
public sealed class ConversationStoreOptions
{
    public const string SectionName = "Beacon:Conversations";

    /// <summary>
    /// The folder that holds one subfolder per user and thread. Relative paths are under the content root. In a
    /// container it must be a mounted volume, or every new revision starts with no conversations.
    /// </summary>
    public string Directory { get; set; } = "App_Data/conversations";

    /// <summary>A thread nobody has touched for this long is deleted, messages and all.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>The raw event log is kept for this long after its last write; the run records stay.</summary>
    public int EventLogRetentionDays { get; set; } = 30;
}

/// <summary>
/// A durable store in plain files, as in Chapter 22. Each conversation gets a folder named by a hash of the user
/// and the thread (both come from outside, so neither becomes a path), holding three files:
/// <list type="bullet">
/// <item><c>thread.json</c>, the header, replaced atomically (write a temporary file, then rename);</item>
/// <item><c>runs.jsonl</c>, one <see cref="RunRecord"/> per line, appended when a run ends;</item>
/// <item><c>events.jsonl</c>, every event every run sent, appended as it goes out.</item>
/// </list>
/// A run is marked open in the header before its first event is written. Loading a thread whose header still
/// names an open run rebuilds what the client was shown from the event log and records the run as broken.
/// </summary>
/// <remarks>
/// One process, one writer: the run lease and this cache live in memory. Several replicas sharing the folder
/// need a database with conditional writes, or routing that keeps each conversation on one replica.
/// </remarks>
public sealed partial class FileConversationStore : IConversationStore, IDisposable
{
    private const string HeaderFile = "thread.json";
    private const string RunsFile = "runs.jsonl";
    private const string EventsFile = "events.jsonl";

    private readonly ConcurrentDictionary<(string UserId, string ThreadId), Conversation> _loaded = new();
    private readonly SemaphoreSlim _loading = new(1, 1);
    private readonly ConversationStoreOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FileConversationStore> _logger;

    public FileConversationStore(IOptions<ConversationStoreOptions> options, IHostEnvironment environment, TimeProvider time, ILogger<FileConversationStore> logger)
    {
        _options = options.Value;
        _time = time;
        _logger = logger;
        Root = Path.GetFullPath(_options.Directory, environment.ContentRootPath);
        System.IO.Directory.CreateDirectory(Root);
    }

    /// <summary>The folder every conversation folder lives in.</summary>
    public string Root { get; }

    /// <summary>The folder for one user's thread.</summary>
    public string FolderFor(string userId, string threadId) =>
        Path.Combine(Root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}\n{threadId}")))[..32]);

    public async ValueTask<Conversation?> FindAsync(string userId, string threadId, CancellationToken cancellationToken)
    {
        var conversation = await LoadOrCachedAsync(userId, threadId, cancellationToken).ConfigureAwait(false);
        return conversation?.LastRunId is not null ? conversation : null;
    }

    public async ValueTask<Conversation> GetOrCreateAsync(string userId, string threadId, CancellationToken cancellationToken) =>
        await LoadOrCachedAsync(userId, threadId, cancellationToken).ConfigureAwait(false)
        ?? _loaded.GetOrAdd((userId, threadId), key => new Conversation(key.UserId, key.ThreadId));

    public async ValueTask<IReadOnlyList<Conversation>> ListForAsync(string userId, CancellationToken cancellationToken)
    {
        List<Conversation> found = [];
        foreach (var folder in System.IO.Directory.EnumerateDirectories(Root))
        {
            if (await ReadHeaderAsync(folder, cancellationToken).ConfigureAwait(false) is { } header
                && header.UserId == userId
                && await FindAsync(header.UserId, header.ThreadId, cancellationToken).ConfigureAwait(false) is { } conversation)
            {
                found.Add(conversation);
            }
        }

        return [.. found.OrderByDescending(c => c.UpdatedAt)];
    }

    public async ValueTask<bool> TryDeleteAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        using var lease = conversation.TryBeginRun();
        if (lease is null)
        {
            return false;
        }

        _loaded.TryRemove((conversation.UserId, conversation.ThreadId), out _);
        var folder = FolderFor(conversation.UserId, conversation.ThreadId);
        if (System.IO.Directory.Exists(folder))
        {
            System.IO.Directory.Delete(folder, recursive: true);
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    public async ValueTask<IRunJournal> BeginRunAsync(Conversation conversation, string runId, IReadOnlyList<AGUIMessage> accepted, CancellationToken cancellationToken)
    {
        var folder = FolderFor(conversation.UserId, conversation.ThreadId);
        System.IO.Directory.CreateDirectory(folder);

        // The header says a run is open before its first event is written, so a crash in between is noticed.
        var started = _time.GetUtcNow();
        var header = HeaderOf(conversation) with { OpenRun = new OpenRun(runId, started, [.. accepted]) };
        await WriteHeaderAsync(folder, header, cancellationToken).ConfigureAwait(false);

        var log = new FileStream(Path.Combine(folder, EventsFile), FileMode.Append, FileAccess.Write, FileShare.Read);
        return new RunJournal(this, conversation, folder, runId, started, accepted, log, _time);
    }

    /// <summary>Deletes conversations and event logs older than the retention settings allow. Returns what it removed.</summary>
    public async ValueTask<(int Threads, int EventLogs)> ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        int threads = 0, logs = 0;

        foreach (var folder in System.IO.Directory.EnumerateDirectories(Root))
        {
            if (await ReadHeaderAsync(folder, cancellationToken).ConfigureAwait(false) is not { } header)
            {
                continue;
            }

            if (header.UpdatedAt < now.AddDays(-_options.RetentionDays))
            {
                var conversation = await GetOrCreateAsync(header.UserId, header.ThreadId, cancellationToken).ConfigureAwait(false);
                threads += await TryDeleteAsync(conversation, cancellationToken).ConfigureAwait(false) ? 1 : 0;
                continue;
            }

            var events = Path.Combine(folder, EventsFile);
            if (header.OpenRun is null && File.Exists(events)
                && new DateTimeOffset(File.GetLastWriteTimeUtc(events), TimeSpan.Zero) < now.AddDays(-_options.EventLogRetentionDays))
            {
                File.Delete(events);
                logs++;
            }
        }

        return (threads, logs);
    }

    public void Dispose() => _loading.Dispose();

    // ---- Writing --------------------------------------------------------------------------------

    internal async ValueTask CommitAsync(Conversation conversation, string folder, RunRecord record, CancellationToken cancellationToken)
    {
        // 1. The record, flushed to the disk.
        await using (var runs = new FileStream(Path.Combine(folder, RunsFile), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            await runs.WriteAsync(Line(record, ConversationJson.TypeInfo<RunRecord>()), cancellationToken).ConfigureAwait(false);
            runs.Flush(flushToDisk: true);
        }

        // 2. Then the header that makes it the thread's latest run (the conversation already says so in memory).
        await WriteHeaderAsync(folder, HeaderOf(conversation), cancellationToken).ConfigureAwait(false);
    }

    private ConversationHeader HeaderOf(Conversation conversation) => new()
    {
        UserId = conversation.UserId,
        ThreadId = conversation.ThreadId,
        CreatedAt = conversation.CreatedAt,
        UpdatedAt = conversation.UpdatedAt == default ? _time.GetUtcNow() : conversation.UpdatedAt,
        LastRunId = conversation.LastRunId,
        LastRunEnding = conversation.LastRunEnding,
        LastRunError = conversation.LastRunError?.Message,
        LastRunErrorCode = conversation.LastRunError?.Code,
        PendingClientCalls = new Dictionary<string, string>(conversation.PendingClientCalls, StringComparer.Ordinal),
    };

    private static async ValueTask WriteHeaderAsync(string folder, ConversationHeader header, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, HeaderFile);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, header, ConversationJson.TypeInfo<ConversationHeader>(), cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        // A rename replaces the header in one step: a reader sees the old one or the new one, never half of either.
        File.Move(temporary, path, overwrite: true);
    }

    internal static byte[] Line<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        [.. JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), (byte)'\n'];

    // ---- Loading --------------------------------------------------------------------------------

    private async ValueTask<Conversation?> LoadOrCachedAsync(string userId, string threadId, CancellationToken cancellationToken)
    {
        if (_loaded.TryGetValue((userId, threadId), out var conversation))
        {
            return conversation;
        }

        await _loading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded.TryGetValue((userId, threadId), out conversation))
            {
                return conversation;
            }

            conversation = await LoadAsync(FolderFor(userId, threadId), cancellationToken).ConfigureAwait(false);
            if (conversation is not null)
            {
                _loaded[(userId, threadId)] = conversation;
            }

            return conversation;
        }
        finally
        {
            _loading.Release();
        }
    }

    private async ValueTask<Conversation?> LoadAsync(string folder, CancellationToken cancellationToken)
    {
        if (await ReadHeaderAsync(folder, cancellationToken).ConfigureAwait(false) is not { } header)
        {
            return null;
        }

        var runs = await ReadLinesAsync(Path.Combine(folder, RunsFile), ConversationJson.TypeInfo<RunRecord>(), cancellationToken).ConfigureAwait(false);
        var conversation = new Conversation(header.UserId, header.ThreadId) { CreatedAt = header.CreatedAt };
        foreach (var run in runs)
        {
            conversation.Append(run.Input);
            conversation.Append(run.Output);
            conversation.Recorded(run.RunId, run.EndedAt ?? run.StartedAt, run.Ending, run.Error is null ? null : (run.Error, run.ErrorCode));
        }

        foreach (var (callId, name) in header.PendingClientCalls)
        {
            conversation.PendingClientCalls[callId] = name;
        }

        // The process stopped while a run was streaming: rebuild what the client was shown from the event log.
        if (header.OpenRun is { } open && runs.All(r => r.RunId != open.RunId))
        {
            var record = await RecoverAsync(folder, open, conversation, cancellationToken).ConfigureAwait(false);
            conversation.PendingClientCalls.Clear(); // the run started, so earlier calls were no longer awaited
            conversation.Append(record.Input);
            conversation.Append(record.Output);
            conversation.Recorded(record.RunId, record.EndedAt ?? record.StartedAt, RunEnding.Broken);
            await CommitAsync(conversation, folder, record, cancellationToken).ConfigureAwait(false);
            LogRecovered(_logger, open.RunId, record.Output.Count);
        }
        else if (header.OpenRun is not null)
        {
            await WriteHeaderAsync(folder, HeaderOf(conversation), cancellationToken).ConfigureAwait(false);
        }

        return conversation;
    }

    private async ValueTask<RunRecord> RecoverAsync(string folder, OpenRun open, Conversation conversation, CancellationToken cancellationToken)
    {
        var lines = await ReadLinesAsync(Path.Combine(folder, EventsFile), ConversationJson.TypeInfo<StoredEvent>(), cancellationToken).ConfigureAwait(false);
        var events = lines.Where(l => l.RunId == open.RunId).ToList();

        var recorder = new TranscriptRecorder(conversation.Messages
            .OfType<AGUIAssistantMessage>()
            .SelectMany(m => m.ToolCalls ?? [])
            .Select(c => c.Id));
        foreach (var stored in events)
        {
            if (ConversationJson.ToEvent(stored.Event) is { } evt and not (RunFinishedEvent or RunErrorEvent))
            {
                recorder.Observe(evt);
            }
        }

        // The process stopped under the run, whatever its last event said: a broken run, restated as RUN_ERROR.
        return new RunRecord
        {
            RunId = open.RunId,
            StartedAt = open.StartedAt,
            EndedAt = events.Count > 0 ? events[^1].At : open.StartedAt,
            Ending = RunEnding.Broken,
            Input = open.Input,
            Output = [.. recorder.Messages],
        };
    }

    private static async ValueTask<ConversationHeader?> ReadHeaderAsync(string folder, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, HeaderFile);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync(stream, ConversationJson.TypeInfo<ConversationHeader>(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<List<T>> ReadLinesAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        List<T> items = [];
        if (!File.Exists(path))
        {
            return items;
        }

        var lineNumber = 0;
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize(line, typeInfo) is { } item)
                {
                    items.Add(item);
                }
            }
            catch (JsonException)
            {
                // A torn write: the process stopped halfway through a line. Everything before it is intact.
                LogSkippedLine(_logger, Path.GetFileName(path), lineNumber);
            }
        }

        return items;
    }

    [LoggerMessage(EventId = 2711, Level = LogLevel.Warning, Message = "Rebuilt run {RunId} from its event log ({Messages} message(s)); the process stopped before recording it.")]
    private static partial void LogRecovered(ILogger logger, string runId, int messages);

    [LoggerMessage(EventId = 2712, Level = LogLevel.Warning, Message = "Skipped unreadable line {Line} of {File}.")]
    private static partial void LogSkippedLine(ILogger logger, string file, int line);

    /// <summary>One run on its way to disk.</summary>
    private sealed class RunJournal(
        FileConversationStore store,
        Conversation conversation,
        string folder,
        string runId,
        DateTimeOffset startedAt,
        IReadOnlyList<AGUIMessage> accepted,
        FileStream log,
        TimeProvider time) : IRunJournal
    {
        private bool _completed;

        public async ValueTask AppendAsync(BaseEvent evt, CancellationToken cancellationToken)
        {
            var line = Line(new StoredEvent(runId, time.GetUtcNow(), ConversationJson.ToStoredJson(evt)), ConversationJson.TypeInfo<StoredEvent>());
            await log.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await log.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask CommitAsync(RunEnding ending, (string Message, string? Code)? error, IReadOnlyList<AGUIMessage> output, CancellationToken cancellationToken)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            log.Flush(flushToDisk: true);
            await log.DisposeAsync().ConfigureAwait(false);

            await store.CommitAsync(conversation, folder, new RunRecord
            {
                RunId = runId,
                StartedAt = startedAt,
                EndedAt = time.GetUtcNow(),
                Ending = ending,
                Error = error?.Message,
                ErrorCode = error?.Code,
                Input = [.. accepted],
                Output = [.. output],
            }, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => log.DisposeAsync();
    }
}
