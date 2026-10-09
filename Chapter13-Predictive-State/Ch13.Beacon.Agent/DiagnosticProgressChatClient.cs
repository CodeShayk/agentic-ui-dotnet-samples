using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace Ch13.Beacon.Agent;

/// <summary>
/// Reports how a run is going, from what flows through it: <c>STEP_STARTED</c> and
/// <c>STEP_FINISHED</c> around its phases (planning, investigating, reporting) and an activity
/// message, "What Beacon checked", that gains a line for every lookup and fills in as each returns.
/// Both go out through <see cref="ChatResponseUpdate.RawRepresentation"/>, which <c>AGUI.Server</c>
/// emits verbatim.
/// </summary>
/// <remarks>
/// It sits above <c>UseFunctionInvocation()</c>, so it sees every model turn of the run, every tool
/// result and the end of the run. Its memory (the open phase, the checks so far) lives in the
/// iterator, so it is per run by construction. Every step it opens is closed before the stream ends,
/// as the protocol requires; if the run fails instead, <c>RUN_ERROR</c> ends whatever is open.
/// </remarks>
public sealed class DiagnosticProgressChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var runId = options is not null && options.TryGetRunAgentInput(out var input) ? input.RunId : Guid.NewGuid().ToString("N");
        var phases = new PhaseTracker();
        var checks = new CheckList($"checks-{runId}");

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            // A phase starts before the first call that belongs to it: the step brackets the call.
            foreach (var tool in ToolsStartingIn(update))
            {
                foreach (var evt in phases.Enter(PhaseTracker.PhaseOf(tool)))
                {
                    yield return Emit(evt);
                }
            }

            yield return update;

            // A check is added once its call is complete, and filled in when its result arrives.
            foreach (var evt in checks.Observe(update))
            {
                yield return Emit(evt);
            }
        }

        foreach (var evt in phases.Finish())
        {
            yield return Emit(evt);
        }
    }

    private static ChatResponseUpdate Emit(BaseEvent evt) => new() { RawRepresentation = evt };

    // A call announces itself with its first argument fragment (streaming providers) or as a complete
    // call (providers that send calls whole). Either way it carries the tool's name.
    private static IEnumerable<string> ToolsStartingIn(ChatResponseUpdate update) =>
        (ToolArgumentFragments.Extract(update) ?? [])
            .Select(f => f.FunctionName)
            .Concat(update.Contents.OfType<FunctionCallContent>().Select(c => c.Name))
            .OfType<string>()
            .Where(name => name.Length > 0);
}

/// <summary>Opens and closes the run's phases as <c>STEP_STARTED</c> / <c>STEP_FINISHED</c> pairs.</summary>
internal sealed class PhaseTracker
{
    public const string Planning = "planning";
    public const string Investigating = "investigating";
    public const string Reporting = "reporting";

    private string? _open;

    /// <summary>The phase a tool belongs to; <see langword="null"/> for bookkeeping that belongs to whatever phase is open.</summary>
    public static string? PhaseOf(string tool) => tool switch
    {
        DiagnosticTools.SetPlanName => Planning,
        DiagnosticTools.UpdatePlanStepName => null,
        DiagnosticTools.WriteReportName => Reporting,
        _ => Investigating, // every lookup
    };

    public IEnumerable<BaseEvent> Enter(string? phase)
    {
        if (phase is null || phase == _open)
        {
            return [];
        }

        List<BaseEvent> events = [];
        if (_open is not null)
        {
            events.Add(new StepFinishedEvent { StepName = _open });
        }

        events.Add(new StepStartedEvent { StepName = phase });
        _open = phase;
        return events;
    }

    public IEnumerable<BaseEvent> Finish()
    {
        if (_open is null)
        {
            return [];
        }

        var finished = new StepFinishedEvent { StepName = _open };
        _open = null;
        return [finished];
    }
}
