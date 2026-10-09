using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AGUI.Abstractions;
using Ch18.Beacon.Agent.Approvals;
using Ch18.Beacon.Agent.Specialists;
using Ch18.Beacon.Agent.Subagents;
using Microsoft.Extensions.AI;

namespace Ch18.Beacon.Agent.Coordination;

/// <summary>
/// Beacon's coordinator. Its model plans by calling one delegation tool per specialist; the coordinator
/// runs those calls itself, all at once, and streams every specialist's work as it happens, attributed
/// to that invocation; then its model answers from the reports. Its own phases are steps.
/// </summary>
/// <remarks>
/// <para>
/// The delegation calls are the coordinator's plumbing and never reach the wire: what the user sees is
/// who worked (<c>SUBAGENT_*</c> and attributed events), not the calls that set them going. They are not
/// left to <see cref="FunctionInvokingChatClient"/>, because it yields nothing while tools run and hands
/// back every result of a round together (Chapter 9): three specialists would appear in one burst.
/// </para>
/// <para>
/// A specialist that fails ends its own invocation with <c>SUBAGENT_ERROR</c>, and the run carries on. A
/// specialist that stops for an approval ends its invocation as suspended and is kept on the server; when
/// the dispatcher answers, the next run continues it under the same <c>subagentRunId</c>.
/// </para>
/// </remarks>
public sealed partial class BeaconCoordinator(
    IChatClient model,
    SpecialistTeam team,
    ISuspensionStore suspensions,
    IApprovalStore approvals,
    ILogger<BeaconCoordinator> logger) : DelegatingChatClient(model)
{
    public const string Planning = "planning";
    public const string Consulting = "consulting";
    public const string Resuming = "resuming";
    public const string Answering = "answering";

    // How AGUI.Server marks a history message a subagent produced (AGUIChatMessageExtensions).
    private const string SubagentRunIdKey = "agui.subagentRunId";

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var run = CoordinatorRun.From(options) ?? throw new InvalidOperationException("The coordinator needs its run on the chat options.");

        // The coordinator's model reads the parent's side of the conversation. The specialists' messages come
        // back in the client's history attributed to them; they were the specialists' work, not its own.
        List<ChatMessage> history = [.. messages.Where(m => m.AdditionalProperties?.ContainsKey(SubagentRunIdKey) != true)];

        List<Delegation> delegations = [.. Continuations(run)];
        List<ChatResponseUpdate> direct = [];

        if (run.Decided.Count == 0)
        {
            // 1. Plan: the model calls one delegation tool per specialist it needs.
            yield return Emit(new StepStartedEvent { StepName = Planning });
            await foreach (var update in base.GetStreamingResponseAsync(history, CoordinatorOptions(options, ChatToolMode.Auto), cancellationToken).ConfigureAwait(false))
            {
                foreach (var call in update.Contents.OfType<FunctionCallContent>())
                {
                    if (team.ForDelegationTool(call.Name) is { } specialist && call.Arguments?.TryGetValue("task", out var task) is true && task?.ToString() is { Length: > 0 } brief)
                    {
                        delegations.Add(Delegation.Start(specialist, call, brief));
                    }
                }

                direct.Add(update);
            }

            yield return Emit(new StepFinishedEvent { StepName = Planning });
        }

        // 2. Consult (or resume): every specialist at once, each in its own lane.
        List<DelegationResult> results = [];
        if (delegations.Count > 0)
        {
            var phase = run.Decided.Count > 0 ? Resuming : Consulting;
            yield return Emit(new StepStartedEvent { StepName = phase });
            await foreach (var update in ConsultAsync(delegations, run, results, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            yield return Emit(new StepFinishedEvent { StepName = phase });
        }

        // 3. Answer from the reports. If the model answered without delegating, that was the answer.
        yield return Emit(new StepStartedEvent { StepName = Answering });
        if (results.Count == 0 && direct.Count > 0)
        {
            foreach (var update in direct)
            {
                // A delegation call the coordinator couldn't use (no task, say) is still plumbing: it never
                // reaches the wire, where it would be a call nobody answers.
                if (update.Contents.Any(c => c is FunctionCallContent))
                {
                    update.Contents = [.. update.Contents.Where(c => c is not FunctionCallContent)];
                }

                yield return update;
            }
        }
        else
        {
            List<ChatMessage> input = [.. history];
            if (results.Count > 0)
            {
                input.Add(new ChatMessage(ChatRole.Assistant, [.. results.Select(r => (AIContent)r.Delegation.Call)]));
                input.Add(new ChatMessage(ChatRole.Tool, [.. results.Select(r => (AIContent)new FunctionResultContent(r.Delegation.Call.CallId, r.Report))]));
            }

            await foreach (var update in base.GetStreamingResponseAsync(input, CoordinatorOptions(options, ChatToolMode.None), cancellationToken).ConfigureAwait(false))
            {
                // The answer turn delegates nothing; a stray call from a real model would be a call nobody answers.
                if (update.Contents.Any(c => c is FunctionCallContent))
                {
                    update.Contents = [.. update.Contents.Where(c => c is not FunctionCallContent)];
                }

                yield return update;
            }
        }

        yield return Emit(new StepFinishedEvent { StepName = Answering });
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    // Runs every delegation on its own task and merges their events into one stream, in the order they happen.
    private async IAsyncEnumerable<ChatResponseUpdate> ConsultAsync(
        IReadOnlyList<Delegation> delegations,
        CoordinatorRun run,
        List<DelegationResult> results,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var merged = Channel.CreateUnbounded<ChatResponseUpdate>(new UnboundedChannelOptions { SingleReader = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var work = delegations.Select(d => Task.Run(() => InvokeAsync(d, run, merged.Writer, stop.Token), CancellationToken.None)).ToArray();
        var all = Task.WhenAll(work);
        _ = all.ContinueWith(_ => merged.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            await foreach (var update in merged.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            // A client that goes away mid-run stops every specialist with it.
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(work.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default))).ConfigureAwait(false);
        }

        results.AddRange(await all.ConfigureAwait(false));
    }

    // One invocation, start to finish. It never throws for the specialist's own failures: those become
    // SUBAGENT_ERROR and a failed report, and the other specialists and the run carry on.
    private async Task<DelegationResult> InvokeAsync(Delegation delegation, CoordinatorRun run, ChannelWriter<ChatResponseUpdate> output, CancellationToken cancellationToken)
    {
        var (specialist, invocation) = (delegation.Specialist, delegation.Invocation);
        var events = new AttributedEventWriter(invocation.SubagentRunId, delegation.AnnouncedCalls);
        List<ChatResponseUpdate> received = [];

        await output.WriteAsync(invocation.Started(), cancellationToken).ConfigureAwait(false);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(specialist.Timeout);

        string? failure = null;
        string? code = null;
        try
        {
            var options = specialist.OptionsFor(run.Approval with { SubagentRunId = invocation.SubagentRunId });
            await foreach (var update in specialist.Client.GetStreamingResponseAsync(delegation.Messages, options, budget.Token).ConfigureAwait(false))
            {
                received.Add(update);
                foreach (var evt in events.Write(update))
                {
                    await output.WriteAsync(Emit(evt), cancellationToken).ConfigureAwait(false);
                }

                // Usage isn't an event; passed up as content, it adds to the run's total on RUN_FINISHED.
                if (update.Contents.OfType<UsageContent>().ToList() is { Count: > 0 } usage)
                {
                    await output.WriteAsync(new ChatResponseUpdate { Contents = [.. usage], ModelId = update.ModelId }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            (failure, code) = ($"The {specialist.Label.ToLowerInvariant()} didn't answer within {Seconds(specialist.Timeout)}.", "timeout");
            LogTimedOut(specialist.Name, invocation.SubagentRunId, specialist.Timeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            (failure, code) = ($"The {specialist.Label.ToLowerInvariant()} ran into a problem and stopped.", "specialist_failed");
            LogFailed(ex, specialist.Name, invocation.SubagentRunId);
        }

        foreach (var evt in events.Complete(failure))
        {
            await output.WriteAsync(Emit(evt), cancellationToken).ConfigureAwait(false);
        }

        if (failure is not null)
        {
            await output.WriteAsync(invocation.Failed(failure, code), cancellationToken).ConfigureAwait(false);
            return new DelegationResult(delegation, Report(delegation, "failed", error: failure));
        }

        var response = received.ToChatResponse();
        var said = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Text.Length > 0)?.Text ?? string.Empty;

        if (events.InterruptIds.Count > 0)
        {
            // Kept on the server: the continuation is rebuilt from this, not from the client's copy.
            suspensions.Save(new SuspendedInvocation(run.ThreadId, invocation.SubagentRunId, specialist.Name, delegation.Call.CallId,
                [.. delegation.Messages, .. WithoutApprovalRequests(response.Messages)]));
            await output.WriteAsync(invocation.Suspended(events.InterruptIds), cancellationToken).ConfigureAwait(false);

            var waitingFor = string.Join(" ", events.InterruptIds.Select(id => approvals.Find(run.ThreadId, id)?.Summary));
            return new DelegationResult(delegation, Report(delegation, "waiting_for_approval", said, waitingFor: waitingFor));
        }

        await output.WriteAsync(invocation.Finished(), cancellationToken).ConfigureAwait(false);
        return new DelegationResult(delegation, Report(delegation, "completed", said));
    }

    // The invocations the dispatcher's answers unblock, rebuilt from the server's records.
    private IEnumerable<Delegation> Continuations(CoordinatorRun run)
    {
        foreach (var decided in run.Decided.GroupBy(a => a.SubagentRunId))
        {
            if (decided.Key is not { } subagentRunId
                || suspensions.Take(run.ThreadId, subagentRunId) is not { } waiting
                || team.Named(waiting.Specialist) is not { } specialist)
            {
                LogNothingToResume(run.ThreadId, decided.Key ?? "(coordinator)");
                continue;
            }

            var call = new FunctionCallContent(waiting.DelegationCallId, specialist.DelegationToolName,
                new Dictionary<string, object?> { ["task"] = "Continue after the dispatcher's decision." });

            // Chapter 14's exchange, from the server's records, appended to the specialist's own conversation.
            yield return new Delegation(
                specialist,
                new SubagentInvocation(specialist.Info, subagentRunId),
                call,
                [.. waiting.Messages, .. ApprovalMessages.For([.. decided])],
                [.. decided.Select(a => a.ToolCallId)],
                Resumed: true);
        }
    }

    private ChatOptions CoordinatorOptions(ChatOptions? options, ChatToolMode toolMode)
    {
        var coordinator = options?.Clone() ?? new ChatOptions();
        coordinator.Instructions = SpecialistInstructions.Coordinator;
        coordinator.Tools = team.DelegationTools; // only delegation: the coordinator looks nothing up itself
        coordinator.ToolMode = toolMode;
        return coordinator;
    }

    private static JsonElement Report(Delegation delegation, string status, string? report = null, string? error = null, string? waitingFor = null)
    {
        var json = new JsonObject
        {
            ["specialist"] = delegation.Specialist.Name,
            ["label"] = delegation.Specialist.Label,
            ["status"] = status,
        };

        if (report is not null)
        {
            json["report"] = report;
        }

        if (error is not null)
        {
            json["error"] = error;
        }

        if (waitingFor is not null)
        {
            json["waitingFor"] = waitingFor;
        }

        if (delegation.Resumed)
        {
            json["resumed"] = true;
        }

        return JsonDocument.Parse(json.ToJsonString()).RootElement.Clone();
    }

    private static IEnumerable<ChatMessage> WithoutApprovalRequests(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Contents.Any(c => c is ToolApprovalRequestContent))
            {
                var kept = message.Contents.Where(c => c is not ToolApprovalRequestContent).ToList();
                if (kept.Count == 0)
                {
                    continue;
                }

                var copy = message.Clone();
                copy.Contents = kept;
                yield return copy;
            }
            else
            {
                yield return message;
            }
        }
    }

    private static ChatResponseUpdate Emit(BaseEvent evt) => SubagentInvocation.Emit(evt);

    private static string Seconds(TimeSpan time) =>
        time.TotalSeconds == 1 ? "1 second" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{time.TotalSeconds:0.#} seconds");

    [LoggerMessage(EventId = 1801, Level = LogLevel.Warning, Message = "{Specialist} invocation {SubagentRunId} timed out after {Timeout}.")]
    private partial void LogTimedOut(string specialist, string subagentRunId, TimeSpan timeout);

    [LoggerMessage(EventId = 1802, Level = LogLevel.Error, Message = "{Specialist} invocation {SubagentRunId} failed.")]
    private partial void LogFailed(Exception exception, string specialist, string subagentRunId);

    [LoggerMessage(EventId = 1803, Level = LogLevel.Warning, Message = "No suspended invocation on thread {ThreadId} for {SubagentRunId}; the decision was recorded but nothing resumed.")]
    private partial void LogNothingToResume(string threadId, string subagentRunId);

    /// <summary>One specialist invocation the coordinator runs: who, with what conversation, under which ID.</summary>
    private sealed record Delegation(
        Specialist Specialist,
        SubagentInvocation Invocation,
        FunctionCallContent Call,
        IReadOnlyList<ChatMessage> Messages,
        IReadOnlyList<string> AnnouncedCalls,
        bool Resumed = false)
    {
        // A new invocation: the specialist sees only its brief.
        public static Delegation Start(Specialist specialist, FunctionCallContent call, string task) =>
            new(specialist, new SubagentInvocation(specialist.Info), call, [new ChatMessage(ChatRole.User, task)], []);
    }

    private sealed record DelegationResult(Delegation Delegation, JsonElement Report);
}
