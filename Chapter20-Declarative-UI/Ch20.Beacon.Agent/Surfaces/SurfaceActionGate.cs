using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Ch20.Beacon.Agent.Inspections;
using Ch20.DeclarativeUi;
using Microsoft.Extensions.AI;

namespace Ch20.Beacon.Agent.Surfaces;

/// <summary>A user action the server accepted and recorded, ready to be told to the model.</summary>
public sealed record AcceptedAction(SurfaceUserAction Action, string RunId, string Summary)
{
    /// <summary>
    /// The action as the model sees it: an assistant call to <c>log_a2ui_event</c> and its result, appended to
    /// the conversation the way the AG-UI A2UI middleware does it. The result is the server's account of what
    /// it recorded, not the client's values.
    /// </summary>
    public IEnumerable<ChatMessage> ToMessages()
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RunId)))[..16];
        var callId = $"a2ui-action-{id}";
        var arguments = new Dictionary<string, object?>
        {
            ["name"] = Action.Name,
            ["surfaceId"] = Action.SurfaceId,
            ["sourceComponentId"] = Action.SourceComponentId,
        };

        yield return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, A2ui.LogEventToolName, arguments)])
        {
            MessageId = $"a2ui-action-assistant-{id}",
        };

        yield return new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent(callId,
                $"The user performed action \"{Action.Name}\" on surface \"{Action.SurfaceId}\" (component: {Action.SourceComponentId}). " +
                $"Beacon checked it and recorded it on the work order: {Summary}"),
        ])
        {
            MessageId = $"a2ui-action-result-{id}",
        };
    }
}

/// <summary>A refusal, written as a problem response before any event is streamed.</summary>
public sealed record ActionRefusal(int StatusCode, string Title, string Detail, IDictionary<string, string[]>? Errors = null)
{
    public IResult ToResult() => Errors is { Count: > 0 }
        ? Results.ValidationProblem(Errors, Detail, title: Title, statusCode: StatusCode)
        : Results.Problem(Detail, statusCode: StatusCode, title: Title);
}

/// <summary>
/// Checks a run's user action before anything runs. Everything in <c>forwardedProps</c> is the client's
/// word, so the action must name a surface this server issued on this thread, the button that surface
/// declared, in time and once; and every value must fit the template. Only then is it recorded.
/// </summary>
public sealed class SurfaceActionGate(SurfaceLedger ledger, InspectionRules rules, InspectionRecorder recorder)
{
    public (AcceptedAction? Accepted, ActionRefusal? Refusal) Check(RunAgentInput input)
    {
        var forwarded = SurfaceUserAction.Read(input.ForwardedProperties);
        if (!forwarded.IsPresent)
        {
            return (null, null);
        }

        if (forwarded.Action is not { } action)
        {
            return (null, new ActionRefusal(StatusCodes.Status400BadRequest, "Malformed action", forwarded.Problem!));
        }

        if (ledger.Find(input.ThreadId, action.SurfaceId) is not { } record)
        {
            return (null, new ActionRefusal(StatusCodes.Status409Conflict, "Unknown surface", "Beacon didn't show a surface with that ID on this thread."));
        }

        lock (record.Gate)
        {
            if (record.Status == SurfaceRecordStatus.Submitted)
            {
                return (null, new ActionRefusal(StatusCodes.Status409Conflict, "Already submitted", "This form was submitted already; each form is submitted once."));
            }

            if (ledger.Time.GetUtcNow() > record.ExpiresAt)
            {
                return (null, new ActionRefusal(StatusCodes.Status409Conflict, "Form expired", $"This form expired at {record.ExpiresAt:u}. Ask Beacon for a new one."));
            }

            if (action.Name != InspectionRules.SubmitAction || action.SourceComponentId != record.Form.SubmitButtonId)
            {
                return (null, new ActionRefusal(StatusCodes.Status409Conflict, "Unknown action", "That surface has no such action on that component."));
            }

            if (rules.ReadSubmission(record.Form.WorkOrderId, action.Context, out var errors) is not { } submission)
            {
                return (null, new ActionRefusal(StatusCodes.Status400BadRequest, "Invalid inspection", "Some values don't fit the inspection.", errors));
            }

            var summary = recorder.Record(submission);
            record.MarkSubmitted(input.RunId, summary);
            return (new AcceptedAction(action, input.RunId, summary), null);
        }
    }
}
