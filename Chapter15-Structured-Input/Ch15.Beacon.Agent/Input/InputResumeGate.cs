using System.Text.Json;
using AGUI.Abstractions;

namespace Ch15.Beacon.Agent.Input;

/// <summary>Why a run was refused before it started, as an HTTP problem.</summary>
/// <param name="StatusCode">400 for a malformed answer, 409 for one that doesn't fit the thread's open requests.</param>
/// <param name="Title">A short summary.</param>
/// <param name="Detail">The specifics, naming the interrupt.</param>
/// <param name="Errors">For an answer that fails its schema: the problems, by field.</param>
public sealed record RunRejection(int StatusCode, string Title, string Detail, IDictionary<string, string[]>? Errors = null)
{
    public IResult ToResult() => Errors is null
        ? Results.Problem(statusCode: StatusCode, title: Title, detail: Detail)
        : Results.ValidationProblem(Errors, detail: Detail, title: Title);
}

/// <summary>
/// Checks a run's resume entries against the input requests this server recorded, before the run starts.
/// An entry counts only if it answers a request that is open on this thread, in time, with a payload that
/// matches the schema the server sent, and every open request on the thread must be answered or abandoned.
/// Then the decisions are applied all at once, so each request is answered once.
/// </summary>
public sealed class InputResumeGate(IInputRequestStore inputs, TimeProvider time)
{
    public (IReadOnlyList<PendingInput> Decided, RunRejection? Rejection) Check(RunAgentInput input, InputRun run)
    {
        var now = time.GetUtcNow();
        List<InputDecision> decisions = [];

        foreach (var entry in input.Resume ?? [])
        {
            if (decisions.Exists(d => d.InputId == entry.InterruptId))
            {
                return Refuse(400, "Duplicate resume entry", $"Interrupt {entry.InterruptId} is answered twice.");
            }

            // Unknown, replayed, from another thread or already answered: all look the same from here.
            if (inputs.Find(input.ThreadId, entry.InterruptId) is not { Status: InputStatus.Open } open)
            {
                return Refuse(409, "Interrupt not open", $"Interrupt {entry.InterruptId} is not open on this thread.");
            }

            // Abandoning is always allowed, even after expiry: it's how a thread moves past a question nobody answered.
            if (entry.Status == ResumeStatus.Cancelled)
            {
                decisions.Add(new InputDecision(open.Id, InputStatus.Cancelled, run.RunId, now, Metadata: ObjectOrNull(entry.Metadata)));
                continue;
            }

            if (entry.Status != ResumeStatus.Resolved)
            {
                return Refuse(400, "Invalid resume entry", $"The status of interrupt {open.Id} must be resolved or cancelled.");
            }

            if (open.ExpiresAt <= now)
            {
                return Refuse(409, "Interrupt expired",
                    $"Interrupt {open.Id} expired at {InputInterrupts.Timestamp(open.ExpiresAt)}; it can only be abandoned.");
            }

            // The answer is checked against the schema the server sent, kept in its own record. Never against
            // one the client might send back, and never by trusting that the client checked it.
            if (entry.Payload is not { } payload)
            {
                return Refuse(400, "Invalid answer", $"Interrupt {open.Id} was resolved without a payload.");
            }

            if (open.Question.Form.Validate(payload) is { Count: > 0 } errors)
            {
                return (Array.Empty<PendingInput>(), new RunRejection(400, "Invalid answer",
                    $"The answer to interrupt {open.Id} doesn't match its response schema.",
                    errors.GroupBy(e => e.Field.Length == 0 ? "payload" : e.Field)
                          .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal)));
            }

            decisions.Add(new InputDecision(open.Id, InputStatus.Answered, run.RunId, now, payload.Clone(), ObjectOrNull(entry.Metadata)));
        }

        // Omission is not abandonment: every open request on the thread needs an entry first.
        if (inputs.OpenOn(input.ThreadId).FirstOrDefault(i => !decisions.Exists(d => d.InputId == i.Id)) is { } uncovered)
        {
            return Refuse(409, "Interrupt still open", $"Interrupt {uncovered.Id} is waiting for an answer. Answer or abandon it first.");
        }

        // All or nothing, and only once: a second copy of this request, even a concurrent one, finds nothing open.
        if (!inputs.TryDecide(input.ThreadId, decisions, out var decided))
        {
            return Refuse(409, "Interrupt not open", "Another request answered this interrupt first.");
        }

        return (decided, null);

        static (IReadOnlyList<PendingInput>, RunRejection?) Refuse(int status, string title, string detail) =>
            ([], new RunRejection(status, title, detail));
    }

    // Metadata is envelope information about the answer. The server keeps it with the record and acts on none of it.
    private static JsonElement? ObjectOrNull(JsonElement? metadata) =>
        metadata is { ValueKind: JsonValueKind.Object } value ? value.Clone() : null;
}
