using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AGUI.Abstractions;
using AGUI.Formatting;

namespace Ch22.Beacon.Desk.Sessions;

/// <summary>One conversation in the agent's list.</summary>
public sealed record ThreadSummary(
    string ThreadId,
    string Title,
    string? WorkOrderId,
    DateTimeOffset UpdatedAt,
    int RunCount,
    int BranchCount,
    bool AwaitingInput,
    bool Compacted);

/// <summary>One run in a thread's tree.</summary>
public sealed record RunSummary(
    string RunId,
    string? ParentRunId,
    DateTimeOffset StartedAt,
    string Outcome,
    IReadOnlyList<string> InputMessageIds,
    IReadOnlyList<string> OutputMessageIds,
    string? Preview);

/// <summary>What the agent says about a compaction (the Desk only shows it).</summary>
public sealed record CompactionInfo(string ThroughMessageId, int SummarizedMessages, string Summary, int EstimatedTokensBefore, int EstimatedTokensAfter);

/// <summary>A thread's run tree.</summary>
public sealed record ThreadDocument(
    string ThreadId,
    string Title,
    string? WorkOrderId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? HeadRunId,
    IReadOnlyList<RunSummary> Runs,
    IReadOnlyList<string> Leaves,
    CompactionInfo? Compaction);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(IReadOnlyList<ThreadSummary>))]
[JsonSerializable(typeof(ThreadDocument))]
[JsonSerializable(typeof(CompactionInfo))]
internal sealed partial class ThreadApiJsonContext : JsonSerializerContext;

/// <summary>
/// The agent's thread API, beside its AG-UI endpoint: the list of conversations, a thread's run tree and a
/// branch restated as AG-UI events. The client reads the events with the SDK's own SSE formatter, so a
/// restored conversation arrives in exactly the form a live one does.
/// </summary>
public sealed class ThreadClient(HttpClient http, string threadsPath)
{
    public async Task<IReadOnlyList<ThreadSummary>> ListAsync(string? workOrderId, CancellationToken cancellationToken)
    {
        var path = workOrderId is null ? threadsPath : $"{threadsPath}?workOrderId={Uri.EscapeDataString(workOrderId)}";
        return await http.GetFromJsonAsync(path, ThreadApiJsonContext.Default.IReadOnlyListThreadSummary, cancellationToken) ?? [];
    }

    public async Task<ThreadDocument?> GetAsync(string threadId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"{threadsPath}/{Uri.EscapeDataString(threadId)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(ThreadApiJsonContext.Default.ThreadDocument, cancellationToken);
    }

    /// <summary>The branch that ends with <paramref name="runId"/> (the latest when null), as the agent restates it.</summary>
    public async Task<IReadOnlyList<BaseEvent>> ReadEventsAsync(string threadId, string? runId, CancellationToken cancellationToken)
    {
        var path = $"{threadsPath}/{Uri.EscapeDataString(threadId)}/events" + (runId is null ? string.Empty : $"?runId={Uri.EscapeDataString(runId)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SseEventStreamFormatter.ServerSentEventsMediaType));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        List<BaseEvent> events = [];
        await foreach (var evt in new SseEventStreamFormatter().ReadAsync(body, cancellationToken))
        {
            events.Add(evt);
        }

        return events;
    }
}
