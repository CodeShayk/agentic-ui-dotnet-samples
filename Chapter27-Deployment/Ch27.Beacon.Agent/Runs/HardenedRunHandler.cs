// Copied from Chapter 23 (Ch23.Beacon.Agent). Chapter 27 combines it with Chapter 22's durable store, Chapter 24's
// negotiation, ledger and heartbeats, and Chapter 25's usage reporting.
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Formatting;
using AGUI.Server;
using Beacon.AGUI.Hosting;
using Beacon.AI;
using Beacon.Domain;
using Ch27.Beacon.Agent.Security.Approvals;
using Ch27.Beacon.Agent.Security.History;
using Ch27.Beacon.Agent.Security.Identity;
using Ch27.Beacon.Agent.Security.Output;
using Ch27.Beacon.Agent.Security.Tools;
using Ch27.Beacon.Agent.Security.Validation;
using Ch27.Beacon.Agent.Streaming;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ch27.Beacon.Agent.Runs;

/// <summary>
/// One run, in the order the checks must happen: who is calling, which format they can read, whether the request
/// is well formed, whether its state names something the caller may see, whether this run ID is new, whether the
/// thread is free and what the server will actually run. Then Chapter 14's approval exchange and Chapter 4's steps.
/// </summary>
public sealed class HardenedRunHandler(
    IChatClient chatClient,
    IConversationStore conversations,
    RunReconciler reconciler,
    RunLedger ledger,
    IFieldServiceStore store,
    SpendingApprovalPolicy policy,
    IApprovalStore approvals,
    IApprovalAudit audit,
    IEnumerable<IAGUIEventStreamFormatter> formatters,
    TimeProvider time,
    IOptions<JsonOptions> jsonOptions,
    IOptions<RunInputLimits> limits,
    IOptions<StreamingOptions> streaming,
    IOptions<ApprovalOptions> approvalOptions,
    ILogger<HardenedRunHandler> logger)
{
    public async Task<IResult> HandleAsync(RunAgentInput input, HttpContext http)
    {
        var cancellationToken = http.RequestAborted;

        // 1. Identity comes from the validated token, never from the request body (Chapter 23).
        if (BeaconCaller.FromPrincipal(http.User) is not { } caller)
        {
            return Results.Forbid();
        }

        // 2. The wire format, chosen before anything runs: protobuf, SSE or 406 (Chapter 24).
        if (EventStreamNegotiation.Select(http.Request, formatters) is not { } formatter)
        {
            return EventStreamNegotiation.NotAcceptable(http.Request, formatters);
        }

        // 3. Shape and size of what's new; messages the server already holds aren't capped again (Chapter 23).
        var known = await conversations.FindAsync(caller.UserId, input.ThreadId, cancellationToken);
        var errors = RunInputValidator.Validate(input, limits.Value, id => known?.TryGetMessage(id, out _) is true);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, title: "The run input is not valid.");
        }

        // 4. State is a claim: the work order it points at must be one this caller may see.
        var state = input.State is { ValueKind: JsonValueKind.Object } json ? DeskState.Parse(json, (_, _) => { }) : null;
        if (state?.ActiveWorkOrderId is { } active && !CanSee(caller, active))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Work order not available",
                detail: "The state names a work order this user can't open.");
        }

        // 5. A run ID runs once per user: the ledger covers runs in flight and recent ones (Chapter 24), the
        //    store every run it ever recorded (Chapter 22). A retry of a run that reached us is refused, not repeated.
        if (!ledger.TryAccept(caller.UserId, input.ThreadId, input.RunId) || known?.HasRun(input.RunId) is true)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Run already received",
                detail: $"Run {input.RunId} has already been received. Start a new run to try again.");
        }

        // 6. The conversation is keyed by user and thread; one run at a time.
        var conversation = await conversations.GetOrCreateAsync(caller.UserId, input.ThreadId, cancellationToken);
        if (conversation.TryBeginRun() is not { } lease)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Run in progress",
                detail: "Another run on this thread hasn't finished.");
        }

        http.Response.RegisterForDispose(lease); // released even if the stream never starts

        // 7. What will actually run: approvals this user was asked, the server's history and what's new.
        var run = new ApprovalRun(input.ThreadId, input.RunId, BeaconClient.IdOf(http.Request), caller);
        var (plan, rejection) = reconciler.Reconcile(caller, input, conversation, run);
        if (rejection is not null)
        {
            lease.Dispose();
            if (input.Resume is { Count: > 0 })
            {
                audit.Record(new ApprovalAuditEntry(time.GetUtcNow(), ApprovalEvent.Refused, run.ThreadId, run.RunId, run.UserId, run.ClientId,
                    ApprovalId: string.Join(',', input.Resume.Select(r => r.InterruptId)), Reason: rejection.Title));
            }

            return Results.Problem(statusCode: rejection.StatusCode, title: rejection.Title, detail: rejection.Detail);
        }

        foreach (var approval in plan!.Decided)
        {
            audit.Record(ApprovalAuditEntry.For(Kind(approval), approval, time.GetUtcNow(), run));
        }

        // However this run ends (finished, failed or dropped), the answers it carried may then be sent again (Chapter 14).
        if (plan.Decided.Count > 0)
        {
            http.Response.OnCompleted(() =>
            {
                approvals.EndRun(run.UserId, run.ThreadId, run.RunId);
                return Task.CompletedTask;
            });
        }

        // 8. Chapter 14's approval exchange from the server's records, then Chapter 4's steps with this caller's
        //    tools. RUN_FINISHED reports token usage under the model's provider name (Chapter 25).
        var usageProvider = chatClient.GetService<ChatClientMetadata>()?.ProviderName ?? "beacon";
        var context = plan.Input.ToChatRequestContext(jsonOptions.Value.SerializerOptions, new AGUIStreamOptions().WithUsageProvider(usageProvider));
        context.Messages.AddRange(ApprovalMessages.For(plan.Decided));
        context.ChatOptions.Instructions = state?.ActiveWorkOrderId is { } open
            ? $"{BeaconInstructions.Default}\nThe user has work order {open} open on screen."
            : BeaconInstructions.Default;

        var tools = new CallerScopedTools(store, () => caller);
        context.ChatOptions.Tools =
        [
            .. tools.CreateTools(tool => new GrantRequiredAIFunction(tool, policy, approvals, plan.Grants, run, audit, time, logger)),
            .. context.ChatOptions.Tools ?? [],
        ];
        context.ChatOptions.AdditionalProperties![ApprovalRun.Key] = run;

        // 9. The pipeline's RunGovernor bounds the model with the run budget and the drain; the converter follows
        //    the request. SecureEventStream records, journals and shapes; the result writes with heartbeats.
        var updates = chatClient.GetStreamingResponseAsync(context.Messages, context.ChatOptions, cancellationToken);
        var events = updates.AsAGUIEventStreamAsync(context, cancellationToken);

        var scope = new RunScope
        {
            Run = run,
            Lease = lease,
            Plan = plan,
            Approvals = approvals,
            Audit = audit,
            ApprovalOptions = approvalOptions.Value,
            Store = conversations,
        };

        var secured = SecureEventStream.RunAsync(events, scope, time, logger, cancellationToken)
            .WithProtocolVersion(AGUIProtocol.Version); // any RUN_STARTED the early start didn't write (Chapter 19)
        return new KeepAliveEventStreamResult(secured, formatter, streaming.Value.HeartbeatInterval, time);
    }

    private static ApprovalEvent Kind(PendingApproval approval) => approval.Status switch
    {
        _ when approval.Decision?.Replaces is not null => ApprovalEvent.Replayed,
        ApprovalStatus.Approved when approval.Decision?.EditedArguments is not null => ApprovalEvent.Edited,
        ApprovalStatus.Approved => ApprovalEvent.Approved,
        ApprovalStatus.Denied => ApprovalEvent.Denied,
        _ => ApprovalEvent.Cancelled,
    };

    private bool CanSee(BeaconCaller caller, string workOrderId) =>
        store.GetWorkOrder(workOrderId) is { } workOrder
        && (caller.IsDispatcher || string.Equals(workOrder.AssignedTechnicianId, caller.TechnicianId, StringComparison.OrdinalIgnoreCase));
}
