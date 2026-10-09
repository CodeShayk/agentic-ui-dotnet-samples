using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AGUI.Hosting;
using Beacon.AI;
using Beacon.Domain;
using Ch23.Beacon.Security;
using Ch23.Beacon.Security.Approvals;
using Ch23.Beacon.Security.History;
using Ch23.Beacon.Security.Identity;
using Ch23.Beacon.Security.Output;
using Ch23.Beacon.Security.Tools;
using Ch23.Beacon.Security.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ch23.Beacon.Agent;

/// <summary>
/// One hardened run, in the order the checks must happen: who is calling, whether the request is well
/// formed, whether its state names something the caller may see, whether the thread is free, and what
/// the server will actually run. Then Chapter 14's approval exchange and Chapter 4's four steps.
/// </summary>
public sealed class HardenedRunHandler(
    IChatClient chatClient,
    IConversationStore conversations,
    RunReconciler reconciler,
    IFieldServiceStore store,
    SpendingApprovalPolicy policy,
    IApprovalStore approvals,
    IApprovalAudit audit,
    TimeProvider time,
    IOptions<JsonOptions> jsonOptions,
    IOptions<RunInputLimits> limits,
    IOptions<BeaconSecurityOptions> security,
    IOptions<ApprovalOptions> approvalOptions,
    ILogger<HardenedRunHandler> logger)
{
    public IResult Handle(RunAgentInput input, HttpContext http)
    {
        // 1. Identity comes from the validated token, never from the request body.
        if (BeaconCaller.FromPrincipal(http.User) is not { } caller)
        {
            return Results.Forbid();
        }

        // 2. Shape and size of what's new. Messages the server already holds are echoes it won't run, so they
        //    aren't capped again. Anything malformed stops here with a 400 that says where.
        var known = conversations.Find(caller.UserId, input.ThreadId);
        var errors = RunInputValidator.Validate(input, limits.Value, id => known?.TryGetMessage(id, out _) is true);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, title: "The run input is not valid.");
        }

        // 3. State is a claim: the work order it points at must be one this caller may see.
        var state = input.State is { ValueKind: JsonValueKind.Object } json ? DeskState.Parse(json, (_, _) => { }) : null;
        if (state?.ActiveWorkOrderId is { } active && !CanSee(caller, active))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Work order not available",
                detail: "The state names a work order this user can't open.");
        }

        // 4. The conversation is keyed by user and thread; one run at a time.
        var conversation = conversations.GetOrCreate(caller.UserId, input.ThreadId);
        if (conversation.TryBeginRun() is not { } lease)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Run in progress",
                detail: "Another run on this thread hasn't finished.");
        }

        http.Response.RegisterForDispose(lease); // released even if the stream never starts

        // 5. What will actually run: approvals this user was asked, the server's history and what's new.
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

        // 6. Chapter 14's approval exchange from the server's records, then Chapter 4's steps with this
        //    caller's tools, whose consequential ones can spend only this request's grants.
        var context = plan.Input.ToChatRequestContext(jsonOptions.Value.SerializerOptions);
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

        var runTimeout = new CancellationTokenSource(security.Value.RunTimeout, time);
        var modelCancellation = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, runTimeout.Token);
        http.Response.RegisterForDispose(runTimeout);
        http.Response.RegisterForDispose(modelCancellation);

        var updates = chatClient.GetStreamingResponseAsync(context.Messages, context.ChatOptions, modelCancellation.Token);
        var events = updates.AsAGUIEventStreamAsync(context, http.RequestAborted);

        var scope = new RunScope
        {
            Run = run,
            Lease = lease,
            Plan = plan,
            Approvals = approvals,
            Audit = audit,
            ApprovalOptions = approvalOptions.Value,
            RunTimeout = runTimeout,
            RunTimeoutLimit = security.Value.RunTimeout,
        };

        var secured = SecureEventStream.RunAsync(events, scope, time, logger, http.RequestAborted)
            .WithProtocolVersion(AGUIProtocol.Version); // AGUI.Server 1.0.0 doesn't declare it (Chapter 19)
        return AGUIResults.Events(secured, http, http.RequestAborted);
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
