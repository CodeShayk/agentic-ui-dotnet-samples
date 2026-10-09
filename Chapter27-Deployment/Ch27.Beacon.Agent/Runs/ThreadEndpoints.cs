// Copied from Chapter 23 (Ch23.Beacon.Agent), on Chapter 27's asynchronous, durable store.
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AGUI.Abstractions;
using Beacon.AGUI.Hosting;
using Ch27.Beacon.Agent.Security.Approvals;
using Ch27.Beacon.Agent.Security.History;
using Ch27.Beacon.Agent.Security.Hosting;
using Ch27.Beacon.Agent.Security.Identity;
using Microsoft.Extensions.Options;

namespace Ch27.Beacon.Agent.Runs;

/// <summary>One of the caller's conversations, for the Desk's list.</summary>
public sealed record ThreadSummary(string ThreadId, string Title, DateTimeOffset UpdatedAt, int MessageCount, int OpenApprovals);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(IReadOnlyList<ThreadSummary>))]
public sealed partial class ThreadJsonContext : JsonSerializerContext;

/// <summary>
/// Chapter 22's thread API (list, restore and delete), with the two controls it lacked: every route needs the
/// same policy as the agent, and every lookup is the caller's. Another user's thread isn't forbidden, it's
/// not found, so the API can't be used to learn which thread IDs exist.
/// </summary>
public static class ThreadEndpoints
{
    public static RouteGroupBuilder MapThreadEndpoints(this IEndpointRouteBuilder endpoints, string prefix)
    {
        var threads = endpoints.MapGroup(prefix)
            .RequireAuthorization(BeaconPolicies.AgentUser)
            .RequireCors(BeaconEndpointPolicies.DeskCors);

        // GET /agent/threads: the caller's conversations, latest first.
        threads.MapGet("/", async (HttpContext http, IConversationStore conversations, IApprovalStore approvals) =>
        {
            var caller = Caller(http);
            IReadOnlyList<ThreadSummary> mine =
            [
                .. (await conversations.ListForAsync(caller.UserId, http.RequestAborted)).Select(c => new ThreadSummary(
                    c.ThreadId, Title(c), c.UpdatedAt, c.Messages.Count, approvals.OpenOn(caller.UserId, c.ThreadId).Count)),
            ];
            return TypedResults.Json(mine, ThreadJsonContext.Default.IReadOnlyListThreadSummary);
        });

        // GET /agent/threads/{threadId}/events: the caller's conversation restated as AG-UI events, as in Chapter 22.
        threads.MapGet("/{threadId}/events", async (string threadId, HttpContext http, IConversationStore conversations,
            IApprovalStore approvals, IOptions<ApprovalOptions> options) =>
        {
            var caller = Caller(http);
            if (await conversations.FindAsync(caller.UserId, threadId, http.RequestAborted) is not { } conversation)
            {
                return Results.NotFound();
            }

            var events = Restate(conversation, approvals.OpenOn(caller.UserId, threadId), options.Value)
                .WithProtocolVersion(AGUIProtocol.Version);
            return AGUIResults.Events(events, http, http.RequestAborted);
        });

        // DELETE /agent/threads/{threadId}: forget the caller's conversation.
        threads.MapDelete("/{threadId}", async (string threadId, HttpContext http, IConversationStore conversations) =>
        {
            var caller = Caller(http);
            if (await conversations.FindAsync(caller.UserId, threadId, http.RequestAborted) is not { } conversation)
            {
                return Results.NotFound();
            }

            return await conversations.TryDeleteAsync(conversation, http.RequestAborted)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Run in progress", detail: "Try again when the run has finished.");
        });

        return threads;
    }

    // The group's policy guarantees a complete Beacon identity, so this can't fail for a request that got here.
    private static BeaconCaller Caller(HttpContext http) =>
        BeaconCaller.FromPrincipal(http.User) ?? throw new InvalidOperationException("The thread API ran without a Beacon caller.");

    private static string Title(Conversation conversation) =>
        conversation.Messages.OfType<AGUIUserMessage>().FirstOrDefault()?.Content.ToString() is { } first
            ? first.Length <= 80 ? first : string.Concat(first.AsSpan(0, 79), "…")
            : conversation.ThreadId;

    /// <summary>The <c>RUN_ERROR</c> code of a restated run that broke under the server (Chapter 22's).</summary>
    public const string BrokenRunCode = "RUN_INCOMPLETE";

    // RUN_STARTED with the latest run's ID, the transcript as a snapshot, then how the run ended. A finished run's
    // open approvals come back as the interrupts they were, so an answer resumes them as it would have live; a
    // stopped run is cancelled, never success, and a failed or broken one is RUN_ERROR, as in Chapter 22.
    private static async IAsyncEnumerable<BaseEvent> Restate(
        Conversation conversation,
        IReadOnlyList<PendingApproval> open,
        ApprovalOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        yield return new RunStartedEvent { ThreadId = conversation.ThreadId, RunId = conversation.LastRunId! };
        yield return new MessagesSnapshotEvent { Messages = [.. conversation.Messages] };
        yield return conversation.LastRunEnding switch
        {
            RunEnding.Failed => new RunErrorEvent
            {
                Message = conversation.LastRunError?.Message ?? "The run failed.",
                Code = conversation.LastRunError?.Code,
            },
            RunEnding.Cancelled => new RunFinishedEvent
            {
                ThreadId = conversation.ThreadId,
                RunId = conversation.LastRunId!,
                Outcome = new RunFinishedCancelledOutcome(),
            },
            RunEnding.Broken => new RunErrorEvent { Message = "The agent stopped before this run finished.", Code = BrokenRunCode },
            _ => new RunFinishedEvent
            {
                ThreadId = conversation.ThreadId,
                RunId = conversation.LastRunId!,
                Outcome = open.Count > 0
                    ? new RunFinishedInterruptOutcome { Interrupts = [.. open.Select(a => ApprovalInterrupts.For(a, options))] }
                    : new RunFinishedSuccessOutcome(),
            },
        };
    }
}
