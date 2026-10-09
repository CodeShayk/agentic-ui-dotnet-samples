using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AGUI.Hosting;
using Beacon.AI;
using Ch14.Beacon.Agent.Approvals;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ch14.Beacon.Agent;

/// <summary>
/// Chapter 4's four steps with an approval gate in front: resume entries are checked against the
/// server's records before anything runs, and the SDK gets the server's version of each approval,
/// never the client's.
/// </summary>
public sealed class ApprovalRunHandler(
    IChatClient chatClient,
    GatedTools tools,
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
        //    once and in time, and no approval may be left unanswered.
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
                _ when approval.Decision?.Replaces is not null => ApprovalEvent.Replayed,
                ApprovalStatus.Approved when approval.Decision?.EditedArguments is not null => ApprovalEvent.Edited,
                ApprovalStatus.Approved => ApprovalEvent.Approved,
                ApprovalStatus.Denied => ApprovalEvent.Denied,
                _ => ApprovalEvent.Cancelled,
            };
            audit.Record(ApprovalAuditEntry.For(kind, approval, time.GetUtcNow(), run.RunId, run.ClientId));
        }

        // However this run ends (finished, failed or dropped), the answers it carried may then be sent again.
        if (decided.Count > 0)
        {
            http.Response.OnCompleted(() =>
            {
                approvals.EndRun(run.ThreadId, run.RunId);
                return Task.CompletedTask;
            });
        }

        // 2. Chapter 4's steps on input the server vouches for: the client's resume list never reaches the SDK,
        //    which would turn any entry into an approved call. The server's own exchange takes its place.
        var context = WithoutResume(input).ToChatRequestContext(jsonOptions.Value.SerializerOptions);
        context.Messages.AddRange(ApprovalMessages.For(decided));
        context.ChatOptions.Instructions = BeaconInstructions.Default;
        context.ChatOptions.Tools = [.. tools.All, .. (context.ChatOptions.Tools ?? []).Where(t => !tools.Names.Contains(t.Name))];
        context.ChatOptions.AdditionalProperties![ApprovalRun.Key] = run;

        var updates = chatClient.GetStreamingResponseAsync(context.Messages, context.ChatOptions, http.RequestAborted);
        var events = updates.AsAGUIEventStreamAsync(context, http.RequestAborted);

        // 3. The server opens the approvals the run proposed and writes their interrupts itself.
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
