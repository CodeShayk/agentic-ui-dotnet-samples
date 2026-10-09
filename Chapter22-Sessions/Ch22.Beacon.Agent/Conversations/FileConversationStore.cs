using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AGUI.Abstractions;
using Microsoft.Extensions.Options;

namespace Ch22.Beacon.Agent.Conversations;

/// <summary>Where Beacon keeps conversations, and for how long.</summary>
public sealed class ConversationStoreOptions
{
    public const string SectionName = "Beacon:Conversations";

    /// <summary>The folder that holds one subfolder per thread. Relative paths are under the content root.</summary>
    public string Directory { get; set; } = "App_Data/conversations";

    /// <summary>A thread nobody has touched for this long is deleted, messages and all.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>The raw event log is kept for this long after its last write; the run records stay.</summary>
    public int EventLogRetentionDays { get; set; } = 30;
}

/// <summary>Persistent storage for conversations, keyed by thread.</summary>
public interface IConversationStore
{
    /// <summary>The thread, loaded from disk on first use, or <see langword="null"/> if nothing is stored for it.</summary>
    ValueTask<StoredConversation?> FindAsync(string threadId, CancellationToken cancellationToken);

    /// <summary>The stored thread, or a new, empty one that is written when its first run starts.</summary>
    ValueTask<StoredConversation> GetOrCreateAsync(string threadId, CancellationToken cancellationToken);

    /// <summary>Every stored thread.</summary>
    ValueTask<IReadOnlyList<StoredConversation>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Marks a run as started and opens its event log. Call under the thread's <see cref="RunLease"/>.</summary>
    ValueTask<RunWriter> BeginRunAsync(StoredConversation conversation, RunRecord started, CancellationToken cancellationToken);

    /// <summary>Adds a compaction to the thread. Call under the thread's <see cref="RunLease"/>.</summary>
    ValueTask SaveCompactionAsync(StoredConversation conversation, CompactionRecord compaction, CancellationToken cancellationToken);

    /// <summary>Deletes a thread. Returns false while a run is in progress on it.</summary>
    ValueTask<bool> DeleteAsync(string threadId, CancellationToken cancellationToken);

    /// <summary>Deletes threads and event logs older than the retention settings allow. Returns what it removed.</summary>
    ValueTask<(int Threads, int EventLogs)> ApplyRetentionAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A durable store in plain files. Each thread gets a folder named by a hash of its ID (the ID comes off the
/// wire, so it never becomes a path), holding three files:
/// <list type="bullet">
/// <item><c>thread.json</c>, the header, replaced atomically (write a temporary file, then rename);</item>
/// <item><c>runs.jsonl</c>, one <see cref="RunRecord"/> per line, appended when a run ends;</item>
/// <item><c>events.jsonl</c>, every event every run sent, appended as it goes out.</item>
/// </list>
/// Appends are flushed to the disk before the header that points at them is replaced, so a crash leaves at
/// worst a torn last line, which loading skips, or a run that started and was never recorded, which loading
/// rebuilds from the event log.
/// </summary>
public sealed partial class FileConversationStore : IConversationStore, IDisposable
{
    private const string HeaderFile = "thread.json";
    private const string RunsFile = "runs.jsonl";
    private const string EventsFile = "events.jsonl";

    private readonly ConcurrentDictionary<string, StoredConversation> _loaded = new(StringComparer.Ordinal);
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

    /// <summary>The folder every thread folder lives in.</summary>
    public string Root { get; }

    /// <summary>The folder for one thread. A thread ID can hold any characters a client chose; a hash can't.</summary>
    public string FolderFor(string threadId) =>
        Path.Combine(Root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(threadId)))[..32]);

    public async ValueTask<StoredConversation?> FindAsync(string threadId, CancellationToken cancellationToken)
    {
        if (_loaded.TryGetValue(threadId, out var conversation))
        {
            return conversation;
        }

        await _loading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded.TryGetValue(threadId, out conversation))
            {
                return conversation;
            }

            conversation = await LoadAsync(FolderFor(threadId), cancellationToken).ConfigureAwait(false);
            if (conversation is not null)
            {
                _loaded[threadId] = conversation;
            }

            return conversation;
        }
        finally
        {
            _loading.Release();
        }
    }

    public async ValueTask<StoredConversation> GetOrCreateAsync(string threadId, CancellationToken cancellationToken) =>
        await FindAsync(threadId, cancellationToken).ConfigureAwait(false)
        ?? _loaded.GetOrAdd(threadId, id => new StoredConversation(
            new ThreadHeader { ThreadId = id, CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow() }, []));

    public async ValueTask<IReadOnlyList<StoredConversation>> ListAsync(CancellationToken cancellationToken)
    {
        List<StoredConversation> found = [];
        foreach (var folder in System.IO.Directory.EnumerateDirectories(Root))
        {
            if (await ReadHeaderAsync(folder, cancellationToken).ConfigureAwait(false) is { } header
                && await FindAsync(header.ThreadId, cancellationToken).ConfigureAwait(false) is { } conversation)
            {
                found.Add(conversation);
            }
        }

        return found;
    }

    public async ValueTask<RunWriter> BeginRunAsync(StoredConversation conversation, RunRecord started, CancellationToken cancellationToken)
    {
        var folder = FolderFor(conversation.ThreadId);
        System.IO.Directory.CreateDirectory(folder);

        // The header says a run is open before its first event is written, so a crash in between is noticed.
        var header = conversation.Header with { OpenRunId = started.RunId };
        await WriteHeaderAsync(folder, header, cancellationToken).ConfigureAwait(false);
        conversation.Apply(header);

        var log = new FileStream(Path.Combine(folder, EventsFile), FileMode.Append, FileAccess.Write, FileShare.Read);
        return new RunWriter(this, conversation, started, log, _time);
    }

    public async ValueTask SaveCompactionAsync(StoredConversation conversation, CompactionRecord compaction, CancellationToken cancellationToken)
    {
        var header = conversation.Header with { Compactions = [.. conversation.Header.Compactions, compaction] };
        var folder = FolderFor(conversation.ThreadId);
        System.IO.Directory.CreateDirectory(folder);
        await WriteHeaderAsync(folder, header, cancellationToken).ConfigureAwait(false);
        conversation.Apply(header);
    }

    public async ValueTask<bool> DeleteAsync(string threadId, CancellationToken cancellationToken)
    {
        if (await FindAsync(threadId, cancellationToken).ConfigureAwait(false) is not { } conversation)
        {
            return true;
        }

        using var lease = conversation.TryBeginRun();
        if (lease is null)
        {
            return false;
        }

        _loaded.TryRemove(threadId, out _);
        var folder = FolderFor(threadId);
        if (System.IO.Directory.Exists(folder))
        {
            System.IO.Directory.Delete(folder, recursive: true);
        }

        LogDeleted(_logger, threadId);
        return true;
    }

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
                threads += await DeleteAsync(header.ThreadId, cancellationToken).ConfigureAwait(false) ? 1 : 0;
                continue;
            }

            var events = Path.Combine(folder, EventsFile);
            if (header.OpenRunId is null && File.Exists(events)
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

    internal async ValueTask CommitAsync(StoredConversation conversation, RunRecord record, CancellationToken cancellationToken)
    {
        var folder = FolderFor(conversation.ThreadId);

        // 1. The record, flushed to the disk.
        await using (var runs = new FileStream(Path.Combine(folder, RunsFile), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            await runs.WriteAsync(Line(record, ConversationJson.TypeInfo<RunRecord>()), cancellationToken).ConfigureAwait(false);
            runs.Flush(flushToDisk: true);
        }

        // 2. Then the header that makes it the thread's latest run.
        var previous = conversation.Header;
        var firstQuestion = record.Input.OfType<AGUIUserMessage>().Select(m => m.Content.ToString()).FirstOrDefault(t => t.Length > 0);
        var header = previous with
        {
            HeadRunId = record.RunId,
            OpenRunId = null,
            UpdatedAt = record.EndedAt ?? _time.GetUtcNow(),
            RunCount = previous.RunCount + 1,
            Title = previous.Title.Length > 0 || firstQuestion is null ? previous.Title : Shorten(firstQuestion, 72),
            WorkOrderId = previous.WorkOrderId ?? (firstQuestion is null ? null : WorkOrderId().Match(firstQuestion) is { Success: true } m ? m.Value : null),
        };

        await WriteHeaderAsync(folder, header, cancellationToken).ConfigureAwait(false);
        conversation.Apply(header, record);
    }

    private static async ValueTask WriteHeaderAsync(string folder, ThreadHeader header, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, HeaderFile);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, header, ConversationJson.TypeInfo<ThreadHeader>(), cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        // A rename replaces the header in one step: a reader sees the old one or the new one, never half of either.
        File.Move(temporary, path, overwrite: true);
    }

    internal static byte[] Line<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        [.. JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), (byte)'\n'];

    // ---- Loading --------------------------------------------------------------------------------

    private async ValueTask<StoredConversation?> LoadAsync(string folder, CancellationToken cancellationToken)
    {
        if (await ReadHeaderAsync(folder, cancellationToken).ConfigureAwait(false) is not { } header)
        {
            return null;
        }

        var runs = await ReadLinesAsync(Path.Combine(folder, RunsFile), ConversationJson.TypeInfo<RunRecord>(), cancellationToken).ConfigureAwait(false);
        var conversation = new StoredConversation(header, runs);

        // The process stopped while a run was streaming: rebuild what the client was shown from the event log.
        if (header.OpenRunId is { } open && runs.All(r => r.RunId != open))
        {
            var recovered = await RecoverAsync(folder, open, cancellationToken).ConfigureAwait(false);
            await CommitAsync(conversation, recovered, cancellationToken).ConfigureAwait(false);
            LogRecovered(_logger, header.ThreadId, open, recovered.Output.Count);
        }
        else if (header.OpenRunId is not null)
        {
            var closed = header with { OpenRunId = null };
            await WriteHeaderAsync(folder, closed, cancellationToken).ConfigureAwait(false);
            conversation.Apply(closed);
        }

        return conversation;
    }

    private async ValueTask<RunRecord> RecoverAsync(string folder, string runId, CancellationToken cancellationToken)
    {
        var lines = await ReadLinesAsync(Path.Combine(folder, EventsFile), ConversationJson.TypeInfo<StoredEvent>(), cancellationToken).ConfigureAwait(false);
        var events = lines.Where(l => l.RunId == runId).ToList();
        var started = events.Select(e => ConversationJson.ToEvent(e.Event)).OfType<RunStartedEvent>().FirstOrDefault();

        // The stored RUN_STARTED carries the run's input, normalized: only what the run added to the branch.
        var recorder = new TranscriptRecorder(started?.Input?.State, []);
        foreach (var stored in events)
        {
            if (ConversationJson.ToEvent(stored.Event) is { } evt and not (RunFinishedEvent or RunErrorEvent))
            {
                recorder.Observe(evt);
            }
        }

        // The process stopped under the run, whatever its last event said: a broken run, restated as RUN_ERROR.
        // The server no longer holds what it would need to resume it.
        return new RunRecord
        {
            RunId = runId,
            ParentRunId = started?.ParentRunId,
            StartedAt = events.Count > 0 ? events[0].At : _time.GetUtcNow(),
            EndedAt = events.Count > 0 ? events[^1].At : _time.GetUtcNow(),
            ProtocolVersion = started?.ProtocolVersion,
            Ending = RunEnding.Broken,
            Input = started?.Input?.Messages ?? [],
            Output = [.. recorder.Messages],
            State = recorder.State,
        };
    }

    private async ValueTask<ThreadHeader?> ReadHeaderAsync(string folder, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, HeaderFile);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync(stream, ConversationJson.TypeInfo<ThreadHeader>(), cancellationToken).ConfigureAwait(false);
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

    private static string Shorten(string text, int length)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= length ? line : line[..(length - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\bWO-\d{4}\b")]
    private static partial Regex WorkOrderId();

    [LoggerMessage(EventId = 2201, Level = LogLevel.Warning, Message = "Rebuilt run {RunId} on thread {ThreadId} from its event log ({Messages} message(s)); the process stopped before recording it.")]
    private static partial void LogRecovered(ILogger logger, string threadId, string runId, int messages);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Warning, Message = "Skipped unreadable line {Line} of {File}.")]
    private static partial void LogSkippedLine(ILogger logger, string file, int line);

    [LoggerMessage(EventId = 2203, Level = LogLevel.Information, Message = "Deleted thread {ThreadId}.")]
    private static partial void LogDeleted(ILogger logger, string threadId);
}

/// <summary>
/// Writes one run as it streams: every event to the log as it goes out, then the run's record when it ends.
/// </summary>
public sealed class RunWriter : IAsyncDisposable
{
    private readonly FileConversationStore _store;
    private readonly StoredConversation _conversation;
    private readonly FileStream _log;
    private readonly TimeProvider _time;
    private bool _completed;

    internal RunWriter(FileConversationStore store, StoredConversation conversation, RunRecord started, FileStream log, TimeProvider time)
    {
        (_store, _conversation, Started, _log, _time) = (store, conversation, started, log, time);
    }

    /// <summary>The run as it started: its parent, its input and the time.</summary>
    public RunRecord Started { get; }

    /// <summary>Appends one event, without <c>rawEvent</c>, and hands it to the operating system.</summary>
    public async ValueTask AppendAsync(BaseEvent evt, CancellationToken cancellationToken)
    {
        var line = FileConversationStore.Line(new StoredEvent(Started.RunId, _time.GetUtcNow(), ConversationJson.ToStoredJson(evt)), ConversationJson.TypeInfo<StoredEvent>());
        await _log.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await _log.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Flushes the log to the disk, then records the run and makes it the thread's latest.</summary>
    public async ValueTask CompleteAsync(RunRecord record, CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _log.Flush(flushToDisk: true);
        await _log.DisposeAsync().ConfigureAwait(false);
        await _store.CommitAsync(_conversation, record, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _log.DisposeAsync();
}
