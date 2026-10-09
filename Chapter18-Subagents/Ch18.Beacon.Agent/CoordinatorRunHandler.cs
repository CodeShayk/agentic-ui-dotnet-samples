using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AGUI.Hosting;
using Ch18.Beacon.Agent.Approvals;
using Ch18.Beacon.Agent.Coordination;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ch18.Beacon.Agent;

/// <summary>
/// Chapter 4's four steps around the coordinator, with Chapter 14's approval gate in front: resume entries
/// are checked against the server's records before anything runs, and the decisions go to the coordinator,
/// which hands each one to the suspended specialist that asked.
/// </summary>
public sealed class CoordinatorRunHandler(
    IChatClient coordinator,
    ResumeGate gate,
    IApprovalStore approvals,
    IApprovalAudit audit,
    TimeProvider time,
    IOptions<ApprovalOptions> approvalOptions,
    IOptions<JsonOptions> jsonOptions)
{
    public IResult Handle(RunAgentInput input, HttpContext http)
    {
        var run = new ApprovalRun(input.ThreadId, input.RunId, BeaconClient.IdOf(http.Request));

        // 1. Every resume entry must answer an approval this server raised on this thread, exactly as raised,
        //    once and in time, and no approval may be left unanswered (Chapter 14).
        var (decided, rejection) = gate.Check(input, run);
        if (rejection is not null)
        {
            audit.Record(new ApprovalAuditEntry(time.GetUtcNow(), ApprovalEvent.Refused, run.ThreadId, run.RunId, run.ClientId,
                ApprovalId: string.Join(',', (input.Resume ?? []).Select(r => r.InterruptId)), Reason: rejection.Title));
            return Results.Problem(statusCode: rejection.StatusCode, title: rejection.Title, detail: rejection.Detail);
        }

        foreach (var approval in decided)
        {
            var kind = approval.Status switch
            {
                ApprovalStatus.Approved => ApprovalEvent.Approved,
                ApprovalStatus.Denied => ApprovalEvent.Denied,
                _ => ApprovalEvent.Cancelled,
            };
            audit.Record(ApprovalAuditEntry.For(kind, approval, time.GetUtcNow(), run.RunId, run.ClientId));
        }

        // 2. Chapter 4's steps. The client's resume list never reaches the SDK (it would turn any entry into an
        //    approved call); the coordinator gets the server's decisions instead.
        var context = WithoutResume(input).ToChatRequestContext(jsonOptions.Value.SerializerOptions);
        context.ChatOptions.AdditionalProperties![CoordinatorRun.Key] = new CoordinatorRun(run, decided);

        var updates = coordinator.GetStreamingResponseAsync(context.Messages, context.ChatOptions, http.RequestAborted);
        var events = updates.AsAGUIEventStreamAsync(context, http.RequestAborted);

        // 3. The approvals a specialist proposed are opened when the run finishes, and their interrupts,
        //    each naming the invocation that asked, replace the SDK's RUN_FINISHED outcome.
        var stream = ApprovalEventStream.RunAsync(events, run, input, approvals, audit, approvalOptions.Value, time, http.RequestAborted);
        return AGUIResults.Events(stream, http, http.RequestAborted);
    }

    private static RunAgentInput WithoutResume(RunAgentInput input) => new()
    {
        ThreadId = input.ThreadId,
        RunId = input.RunId,
        ParentRunId = input.ParentRunId,
        ProtocolVersion = input.ProtocolVersion,
        Messages = input.Messages,
        Tools = input.Tools,
        Context = input.Context,
        State = input.State,
        ForwardedProperties = input.ForwardedProperties,
    };
}
