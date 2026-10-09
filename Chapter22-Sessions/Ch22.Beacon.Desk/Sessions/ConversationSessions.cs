using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch22.Beacon.Desk.Sessions;

/// <summary>Which version of an answer this is, among the answers to the same question.</summary>
/// <param name="Number">1 for the first answer given.</param>
/// <param name="Count">How many answers the question has.</param>
/// <param name="Previous">The branch to open for the previous version, if there is one.</param>
/// <param name="Next">The branch to open for the next version, if there is one.</param>
public sealed record AnswerVersions(int Number, int Count, string? Previous, string? Next);

/// <summary>
/// The Desk's side of persisted conversations, one per circuit: open a stored branch into the circuit's
/// <see cref="AgentConversation"/>, regenerate an answer by branching, and keep the thread's run tree at hand
/// for both. Nothing here keeps conversation state: the agent's store does, and the view is rebuilt from it.
/// </summary>
public sealed class ConversationSessions(AgentConversation conversation, ThreadClient threads)
{
    private static readonly JsonSerializerOptions AGUIOptions = CreateAGUIOptions();

    /// <summary>The open thread's run tree, or <see langword="null"/> for a conversation the agent hasn't stored yet.</summary>
    public ThreadDocument? Document { get; private set; }

    /// <summary>Raised when <see cref="Document"/> changes.</summary>
    public event Action? DocumentChanged;

    public Task<IReadOnlyList<ThreadSummary>> ListAsync(string workOrderId, CancellationToken cancellationToken = default) =>
        threads.ListAsync(workOrderId, cancellationToken);

    /// <summary>Opens a stored conversation: the latest branch, or the one that ends with <paramref name="runId"/>.</summary>
    public async Task OpenAsync(string threadId, string? runId = null, CancellationToken cancellationToken = default)
    {
        var events = await threads.ReadEventsAsync(threadId, runId, cancellationToken);
        ConversationReplay.Restore(conversation, threadId, events);
        await RefreshAsync(cancellationToken);
    }

    /// <summary>Starts a new conversation on a new thread.</summary>
    public void New()
    {
        conversation.Reset();
        Document = null;
        DocumentChanged?.Invoke();
    }

    /// <summary>Reloads the run tree, for example after a run ended.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Document = conversation.View.ThreadId is { } threadId ? await threads.GetAsync(threadId, cancellationToken) : null;
        DocumentChanged?.Invoke();
    }

    /// <summary>The run that produced a message, if the agent has recorded it.</summary>
    public RunSummary? RunThatWrote(string messageId) =>
        Document?.Runs.FirstOrDefault(r => r.OutputMessageIds.Contains(messageId));

    /// <summary>The user message a run answered, if it answered one (a run that resumed an interrupt didn't).</summary>
    public string? QuestionOf(RunSummary run, ConversationView view) =>
        run.InputMessageIds.LastOrDefault(id => view.FindMessage(id) is UserMessageView);

    /// <summary>True for the last answer a run gave to a question: the place "Regenerate" makes sense.</summary>
    public bool CanRegenerate(string messageId, ConversationView view) =>
        !view.IsBusy && !view.OpenInterrupts.Any()
        && RunThatWrote(messageId) is { } run
        && QuestionOf(run, view) is not null
        && view.Messages.OfType<AssistantMessageView>().LastOrDefault(m => run.OutputMessageIds.Contains(m.Id) && m.Text.Length > 0)?.Id == messageId;

    /// <summary>
    /// Asks the agent to answer the same question again, as a branch beside the answer the user is looking at.
    /// The new run continues from the end of the run before that answer, named by <c>parentRunId</c>; the
    /// agent restates that branch, the reducer drops the old answer and the new one streams in.
    /// </summary>
    public async Task RegenerateAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var view = conversation.View;
        var run = RunThatWrote(messageId) ?? throw new InvalidOperationException($"The agent hasn't recorded message {messageId}.");

        var inputs = run.ParentRunId is { } parent
            ? new RunInputs { ParentRunId = parent }

            // The thread's first answer has no run before it. Sending the history up to the question, with no
            // parentRunId, asks the agent to answer that question again from the start of the thread.
            : new RunInputs { Messages = HistoryThrough(QuestionOf(run, view)!, view) };

        await conversation.ContinueAsync(inputs, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    /// <summary>Which version an answer is, and where its siblings are.</summary>
    public AnswerVersions? VersionsOf(string messageId, ConversationView view)
    {
        if (Document is null || RunThatWrote(messageId) is not { } run || QuestionOf(run, view) is not { } question)
        {
            return null;
        }

        var siblings = Document.Runs
            .Where(r => r.ParentRunId == run.ParentRunId && r.InputMessageIds.Contains(question))
            .OrderBy(r => r.StartedAt)
            .ToList();
        if (siblings.Count < 2)
        {
            return null;
        }

        var index = siblings.FindIndex(r => r.RunId == run.RunId);
        return new AnswerVersions(
            index + 1,
            siblings.Count,
            index > 0 ? LatestLeafUnder(siblings[index - 1].RunId) : null,
            index < siblings.Count - 1 ? LatestLeafUnder(siblings[index + 1].RunId) : null);
    }

    // A version may have been continued; open the newest branch that grew from it.
    private string LatestLeafUnder(string runId)
    {
        var runs = Document!.Runs;
        var current = runId;
        while (runs.Where(r => r.ParentRunId == current).OrderByDescending(r => r.StartedAt).FirstOrDefault() is { } child)
        {
            current = child.RunId;
        }

        return current;
    }

    private static List<AGUIMessage> HistoryThrough(string questionId, ConversationView view)
    {
        var history = ConversationHistory.ToChatMessages(view);
        var end = history.FindIndex(m => m.MessageId == questionId);
        return [.. history.Take(end + 1).AsAGUIMessages(AGUIOptions)];
    }

    private static JsonSerializerOptions CreateAGUIOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver);
        options.MakeReadOnly();
        return options;
    }
}
