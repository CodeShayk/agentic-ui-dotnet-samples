using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ch15.Beacon.Agent.Input;

/// <summary>
/// The input requests the server has raised. Every change is a check-and-set on the current version, so a
/// request moves forward once: two requests racing to answer it can't both win.
/// </summary>
public interface IInputRequestStore
{
    /// <summary>Records a question the model just asked, under a new server-issued ID.</summary>
    PendingInput Propose(InputRun run, FunctionCallContent call, InputQuestion question, DateTimeOffset expiresAt);

    /// <summary>The request with this ID on this thread; <see langword="null"/> if there is none, which is all a forger learns.</summary>
    PendingInput? Find(string threadId, string inputId);

    /// <summary>Requests a run raised and hasn't delivered yet.</summary>
    IReadOnlyList<PendingInput> ProposedIn(string threadId, string runId);

    /// <summary>Requests waiting for an answer on the thread.</summary>
    IReadOnlyList<PendingInput> OpenOn(string threadId);

    /// <summary>Marks a proposed request as delivered to the client.</summary>
    PendingInput Open(PendingInput proposed);

    /// <summary>Withdraws whatever the run proposed and never delivered.</summary>
    IReadOnlyList<PendingInput> Withdraw(string threadId, string runId);

    /// <summary>Applies every decision, or none if any request is no longer open.</summary>
    bool TryDecide(string threadId, IReadOnlyList<InputDecision> decisions, out IReadOnlyList<PendingInput> decided);
}

/// <summary>An in-memory <see cref="IInputRequestStore"/> for one server process.</summary>
public sealed class InMemoryInputRequestStore(TimeProvider time) : IInputRequestStore
{
    private readonly Dictionary<string, PendingInput> _inputs = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public PendingInput Propose(InputRun run, FunctionCallContent call, InputQuestion question, DateTimeOffset expiresAt)
    {
        var input = new PendingInput
        {
            // 128 random bits: an ID nobody can derive from a call ID, a run ID or a counter.
            Id = $"inp_{RandomNumberGenerator.GetHexString(32, lowercase: true)}",
            ThreadId = run.ThreadId,
            RunId = run.RunId,
            ToolCallId = call.CallId,
            ToolName = call.Name,
            Arguments = JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, object?>(),
                AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, object?>))),
            Question = question,
            ProposedAt = time.GetUtcNow(),
            ExpiresAt = expiresAt,
        };

        lock (_gate)
        {
            _inputs.Add(input.Id, input);
        }

        return input;
    }

    public PendingInput? Find(string threadId, string inputId)
    {
        lock (_gate)
        {
            return _inputs.TryGetValue(inputId, out var input) && input.ThreadId == threadId ? input : null;
        }
    }

    public IReadOnlyList<PendingInput> ProposedIn(string threadId, string runId) =>
        Where(i => i.ThreadId == threadId && i.RunId == runId && i.Status == InputStatus.Proposed);

    public IReadOnlyList<PendingInput> OpenOn(string threadId) =>
        Where(i => i.ThreadId == threadId && i.Status == InputStatus.Open);

    public PendingInput Open(PendingInput proposed)
    {
        lock (_gate)
        {
            var current = _inputs[proposed.Id];
            if (current.Status != InputStatus.Proposed)
            {
                throw new InvalidOperationException($"Input request {proposed.Id} is {current.Status}, not proposed.");
            }

            return _inputs[proposed.Id] = current with { Status = InputStatus.Open };
        }
    }

    public IReadOnlyList<PendingInput> Withdraw(string threadId, string runId)
    {
        lock (_gate)
        {
            List<PendingInput> withdrawn = [];
            foreach (var input in _inputs.Values.Where(i => i.ThreadId == threadId && i.RunId == runId && i.Status == InputStatus.Proposed).ToList())
            {
                withdrawn.Add(_inputs[input.Id] = input with { Status = InputStatus.Withdrawn });
            }

            return withdrawn;
        }
    }

    public bool TryDecide(string threadId, IReadOnlyList<InputDecision> decisions, out IReadOnlyList<PendingInput> decided)
    {
        lock (_gate)
        {
            // Check every one first: a request answers all of its interrupts or none of them.
            foreach (var decision in decisions)
            {
                if (!_inputs.TryGetValue(decision.InputId, out var current) || current.ThreadId != threadId || current.Status != InputStatus.Open)
                {
                    decided = [];
                    return false;
                }
            }

            decided = [.. decisions.Select(d => _inputs[d.InputId] = _inputs[d.InputId] with
            {
                Status = d.Outcome,
                Answer = d.Answer,
                AnswerMetadata = d.Metadata,
                DecidedInRun = d.RunId,
                DecidedAt = d.At,
            })];
            return true;
        }
    }

    private List<PendingInput> Where(Func<PendingInput, bool> predicate)
    {
        lock (_gate)
        {
            return [.. _inputs.Values.Where(predicate).OrderBy(i => i.ProposedAt)];
        }
    }
}
