using System.Text.RegularExpressions;
using AGUI.Abstractions;
using Ch22.Beacon.Agent.Conversations;
using Ch22.Beacon.Agent.Readings;
using Microsoft.Extensions.AI;

namespace Ch22.Beacon.Agent.Runs;

/// <summary>Why a run was refused before it started, as an HTTP problem.</summary>
public sealed record RunRejection(int StatusCode, string Title, string Detail)
{
    public IResult ToResult() => Results.Problem(statusCode: StatusCode, title: Title, detail: Detail);
}

/// <summary>What the server will run: where on the thread it continues, and what it adds there.</summary>
public sealed record RunPlan
{
    /// <summary>The run this one continues from the end of; <see langword="null"/> to start a branch at the beginning.</summary>
    public RunRecord? Base { get; init; }

    /// <summary>The branch as the server recorded it, reasoning included: the history for this run.</summary>
    public required IReadOnlyList<AGUIMessage> Branch { get; init; }

    /// <summary>What this run adds: the server's record of any answers, then the client's new messages.</summary>
    public required IReadOnlyList<AGUIMessage> NewMessages { get; init; }

    /// <summary>True when the client's view may not match this branch, so the run starts by restating it.</summary>
    public required bool RestatesBranch { get; init; }
}

/// <summary>Per-run data the pipeline shares with the endpoint, carried in <see cref="ChatOptions.AdditionalProperties"/>.</summary>
public sealed class RunScope
{
    public const string Key = "beacon.run";

    /// <summary>Input interrupts the model raised in this run, with the calls behind them.</summary>
    public List<PendingInput> PendingInputs { get; } = [];

    public static RunScope? From(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(Key, out var value) == true ? value as RunScope : null;
}

/// <summary>
/// Beacon's policy for a client that sends the whole conversation to a server that also stores it. The server's
/// copy is the history. The client's copy is used for two things only: to find where the client is (unless
/// <c>parentRunId</c> says so), and to find what's new, which is whatever comes after the last message the
/// server already holds on that branch. Messages are matched by ID, so a client that resends everything adds
/// nothing twice, and a client that sends only its new message works too.
/// </summary>
public sealed partial class RunPlanner(LineVoltageReadings readings, ILogger<RunPlanner> logger)
{
    public (RunPlan? Plan, RunRejection? Rejection) Plan(StoredConversation conversation, RunAgentInput input)
    {
        IList<AGUIMessage> sent = input.Messages ?? [];

        if (conversation.FindRun(input.RunId) is not null)
        {
            return Reject(StatusCodes.Status409Conflict, "Run ID already used", $"Thread {input.ThreadId} already has a run {input.RunId}. Every run needs a new ID.");
        }

        // 1. Where on the thread does this run continue? parentRunId says so explicitly; otherwise the client's
        //    history does: after the last message the server recognizes. A question resent without its answer
        //    is asked again, beside the old answer. A client that sends nothing the server knows continues the head.
        RunRecord? continued;
        if (!string.IsNullOrEmpty(input.ParentRunId))
        {
            continued = conversation.FindRun(input.ParentRunId);
            if (continued is null)
            {
                return Reject(StatusCodes.Status409Conflict, "Unknown parent run", $"Thread {input.ThreadId} has no run {input.ParentRunId} to continue from.");
            }
        }
        else
        {
            continued = Locate(conversation, sent);
        }

        // 2. The branch, from the server's records. This is the history, whatever the client's copy says.
        var branch = conversation.BranchMessages(continued?.RunId);
        var onBranch = branch.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

        // 3. What's new: the client's messages after the last one the server holds on this branch, each in the
        //    server's own copy if it has one. They stop at an answer from another branch: that is the client
        //    showing a branch this run doesn't continue, not something it wrote.
        var lastKnown = -1;
        for (var i = 0; i < sent.Count; i++)
        {
            if (onBranch.Contains(sent[i].Id))
            {
                lastKnown = i;
            }
        }

        List<AGUIMessage> fresh = [];
        foreach (var message in sent.Skip(lastKnown + 1))
        {
            if (message is AGUIActivityMessage or AGUIReasoningMessage)
            {
                continue; // the server holds the authoritative reasoning, sealed values included; activity stays on the client
            }

            if (conversation.Locate(message.Id) is { IsInput: false })
            {
                LogStoppedAtOtherBranch(logger, input.ThreadId, message.Id);
                break;
            }

            fresh.Add(conversation.FindMessage(message.Id) ?? message);
        }

        // 4. Answers to the interrupts of the run being continued, turned into the server's own record of them.
        var (answers, rejection) = readings.Answer(continued, input.Resume ?? []);
        if (rejection is not null)
        {
            return (null, rejection);
        }

        foreach (var ignored in (input.Resume ?? []).Where(r => continued?.PendingInputs.All(p => p.InterruptId != r.InterruptId) ?? true))
        {
            LogIgnoredResume(logger, input.ThreadId, ignored.InterruptId);
        }

        if (answers.Count == 0 && fresh.Count == 0)
        {
            return Reject(StatusCodes.Status409Conflict, "Nothing to answer",
                "The request adds nothing to the conversation: send a new message, answer the open question, or name the run to branch from with parentRunId.");
        }

        IReadOnlyList<AGUIMessage> added = [.. answers, .. fresh];

        // 5. A branch other than the latest, or a client whose copy differs from the server's, may be showing
        //    something this run doesn't continue. The run restates the branch first, and the client reconciles.
        var restates = continued?.RunId != conversation.Header.HeadRunId || !SameConversation(sent, [.. branch, .. added]);

        return (new RunPlan { Base = continued, Branch = branch, NewMessages = added, RestatesBranch = restates }, null);
    }

    /// <summary>IDs are store keys, so the run input's IDs must be present, well formed and unique.</summary>
    public static Dictionary<string, string[]> Validate(RunAgentInput input)
    {
        Dictionary<string, string[]> errors = [];
        if (!IdFormat().IsMatch(input.ThreadId ?? string.Empty))
        {
            errors["threadId"] = ["Must be 1 to 128 letters, digits or . _ : - characters."];
        }

        if (!IdFormat().IsMatch(input.RunId ?? string.Empty))
        {
            errors["runId"] = ["Must be 1 to 128 letters, digits or . _ : - characters."];
        }

        if (!string.IsNullOrEmpty(input.ParentRunId) && !IdFormat().IsMatch(input.ParentRunId))
        {
            errors["parentRunId"] = ["Must be 1 to 128 letters, digits or . _ : - characters."];
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        var messages = input.Messages ?? [];
        for (var i = 0; i < messages.Count; i++)
        {
            if (string.IsNullOrEmpty(messages[i].Id))
            {
                errors[$"messages[{i}].id"] = ["Required. The server tells new messages from ones it holds by their IDs."];
            }
            else if (!seen.Add(messages[i].Id))
            {
                errors[$"messages[{i}].id"] = ["Duplicate message ID."];
            }
        }

        return errors;
    }

    // The client's last message the server recognizes says where the client is.
    private static RunRecord? Locate(StoredConversation conversation, IList<AGUIMessage> sent)
    {
        for (var i = sent.Count - 1; i >= 0; i--)
        {
            if (conversation.Locate(sent[i].Id) is { } found)
            {
                // An answer: continue after its run. A question: ask it again, from where it was first asked.
                return found.IsInput
                    ? found.Run.ParentRunId is { } parent ? conversation.FindRun(parent) : null
                    : found.Run;
            }
        }

        return conversation.Head;
    }

    // Reasoning and activity never travel back from a client, so they don't count as a difference.
    private static bool SameConversation(IEnumerable<AGUIMessage> sent, IEnumerable<AGUIMessage> server) =>
        sent.Where(IsConversation).Select(m => m.Id).SequenceEqual(server.Where(IsConversation).Select(m => m.Id), StringComparer.Ordinal);

    private static bool IsConversation(AGUIMessage message) => message is not (AGUIReasoningMessage or AGUIActivityMessage);

    private static (RunPlan?, RunRejection?) Reject(int status, string title, string detail) => (null, new RunRejection(status, title, detail));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\-]{0,127}$")]
    private static partial Regex IdFormat();

    [LoggerMessage(EventId = 2211, Level = LogLevel.Information, Message = "Thread {ThreadId}: the client's history continues with {MessageId}, an answer from another branch; the rest is not new input.")]
    private static partial void LogStoppedAtOtherBranch(ILogger logger, string threadId, string messageId);

    [LoggerMessage(EventId = 2212, Level = LogLevel.Warning, Message = "Thread {ThreadId}: ignored a resume entry for {InterruptId}, which the run being continued didn't raise.")]
    private static partial void LogIgnoredResume(ILogger logger, string threadId, string interruptId);
}
