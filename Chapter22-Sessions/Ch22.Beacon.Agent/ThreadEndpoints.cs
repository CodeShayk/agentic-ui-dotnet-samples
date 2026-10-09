using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using Beacon.AGUI.Hosting;
using Ch22.Beacon.Agent.Conversations;
using Ch22.Beacon.Agent.Runs;

namespace Ch22.Beacon.Agent;

/// <summary>
/// The thread API beside the AG-UI endpoint: list conversations, read a thread's run tree, restore a branch as
/// an AG-UI event stream and delete a thread. AG-UI defines the stream, not how a client finds an old
/// conversation, so this part is Beacon's own. It has no authentication: anyone who can reach it can read any
/// conversation, which is Chapter 23's problem.
/// </summary>
public static class ThreadEndpoints
{
    public static IEndpointRouteBuilder MapThreadEndpoints(this IEndpointRouteBuilder endpoints, string prefix)
    {
        var threads = endpoints.MapGroup(prefix);

        // GET /agent/threads?workOrderId=WO-1005: the conversations about one job, latest first.
        threads.MapGet("/", async (string? workOrderId, IConversationStore store, CancellationToken cancellationToken) =>
        {
            var all = await store.ListAsync(cancellationToken);
            IReadOnlyList<ThreadSummary> summaries =
            [
                .. all.Where(c => c.Header.HeadRunId is not null && (workOrderId is null || c.Header.WorkOrderId == workOrderId))
                      .OrderByDescending(c => c.Header.UpdatedAt)
                      .Select(Summarize),
            ];
            return TypedResults.Json(summaries, ConversationJson.TypeInfo<IReadOnlyList<ThreadSummary>>());
        });

        // GET /agent/threads/{threadId}: the run tree, for regenerating and switching branches.
        threads.MapGet("/{threadId}", async (string threadId, IConversationStore store, CancellationToken cancellationToken) =>
            await store.FindAsync(threadId, cancellationToken) is { Header.HeadRunId: not null } conversation
                ? Results.Json(Document(conversation), ConversationJson.TypeInfo<ThreadDocument>())
                : Results.NotFound());

        // GET /agent/threads/{threadId}/events?runId=...: a branch restated as AG-UI events (SSE, or protobuf if
        // asked for), for a client that opens the thread. Without runId, the thread's latest branch.
        threads.MapGet("/{threadId}/events", async (string threadId, string? runId, IConversationStore store, HttpContext http) =>
        {
            if (await store.FindAsync(threadId, http.RequestAborted) is not { } conversation
                || (runId is null ? conversation.Head : conversation.FindRun(runId)) is not { } run)
            {
                return Results.NotFound();
            }

            // Each restated RUN_STARTED keeps the version its run declared; one recorded without a declaration
            // gets this producer's (Chapter 19).
            var events = Stream(Restatements.Run(conversation, run)).WithProtocolVersion(AGUIProtocol.Version);
            return AGUIResults.Events(events, http, http.RequestAborted);
        });

        // DELETE /agent/threads/{threadId}: the user asked for the conversation to be forgotten.
        threads.MapDelete("/{threadId}", async (string threadId, IConversationStore store, CancellationToken cancellationToken) =>
            await store.DeleteAsync(threadId, cancellationToken)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Run in progress", detail: "Try again when the run has finished."));

        return endpoints;
    }

    public static ThreadSummary Summarize(StoredConversation conversation)
    {
        var header = conversation.Header;
        return new ThreadSummary(
            header.ThreadId,
            header.Title,
            header.WorkOrderId,
            header.UpdatedAt,
            header.RunCount,
            conversation.Leaves.Count,
            conversation.Head is { IsInterrupted: true },
            header.Compactions.Count > 0);
    }

    public static ThreadDocument Document(StoredConversation conversation)
    {
        var header = conversation.Header;
        return new ThreadDocument(
            header.ThreadId,
            header.Title,
            header.WorkOrderId,
            header.CreatedAt,
            header.UpdatedAt,
            header.HeadRunId,
            [.. conversation.Runs.Select(r => new RunSummary(
                r.RunId,
                r.ParentRunId,
                r.StartedAt,
                OutcomeOf(r),
                [.. r.Input.Select(m => m.Id)],
                [.. r.Output.Select(m => m.Id)],
                r.Output.OfType<AGUIAssistantMessage>().LastOrDefault(m => !string.IsNullOrEmpty(m.Content))?.Content is { } text
                    ? text.Length <= 96 ? text : text[..95] + "…"
                    : null))],
            [.. conversation.Leaves.Select(r => r.RunId)],
            conversation.CompactionFor(conversation.BranchMessages(header.HeadRunId)));
    }

    private static string OutcomeOf(RunRecord run) => run.Ending switch
    {
        RunEnding.Failed or RunEnding.Broken => "failed",
        RunEnding.Cancelled => "cancelled",
        _ => run.Outcome switch
        {
            RunFinishedInterruptOutcome => "interrupt",
            RunFinishedCancelledOutcome => "cancelled",
            _ => "success",
        },
    };

    private static async IAsyncEnumerable<BaseEvent> Stream(IEnumerable<BaseEvent> events, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var evt in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return evt;
        }
    }
}
