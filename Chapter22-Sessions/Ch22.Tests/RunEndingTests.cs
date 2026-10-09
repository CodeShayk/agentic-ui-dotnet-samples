using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using Ch22.Beacon.Agent.Conversations;
using Ch22.Beacon.Agent.Runs;
using Ch22.Tests.Infrastructure;

namespace Ch22.Tests;

/// <summary>
/// How a run that never sent its terminal event is recorded and restated. A run the server stopped because its
/// client left is cancelled; a run that broke under the server is restated as <c>RUN_ERROR</c>, never as cancelled.
/// </summary>
public sealed class RunEndingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stream_that_breaks_under_the_server_is_recorded_as_broken_and_restated_as_RUN_ERROR()
    {
        using var folder = new TempFolder();
        using var store = StoreTests.Store(folder.Path);
        var (conversation, scope) = await BeginAsync(store, "t-broken");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in RecordingEventStream.RunAsync(Answer("t-broken", thenBreak: true, Ct), scope, Ct))
            {
            }
        });

        var run = Assert.Single(conversation.Runs);
        Assert.Equal(RunEnding.Broken, run.Ending);
        Assert.Equal("Let me check", Assert.IsType<AGUIAssistantMessage>(Assert.Single(run.Output)).Content);

        var error = Assert.IsType<RunErrorEvent>(Restatements.Run(conversation, run).Last());
        Assert.Equal(Restatements.BrokenRunCode, error.Code);
    }

    [Fact]
    public async Task A_stream_the_client_leaves_is_recorded_as_cancelled_and_restated_with_the_cancelled_outcome()
    {
        using var folder = new TempFolder();
        using var store = StoreTests.Store(folder.Path);
        var (conversation, scope) = await BeginAsync(store, "t-left");

        // The client leaves after the first words: the request is aborted and the writer stops reading.
        using var request = new CancellationTokenSource();
        await foreach (var evt in RecordingEventStream.RunAsync(Answer("t-left", thenBreak: false, Ct), scope, request.Token))
        {
            if (evt is TextMessageContentEvent)
            {
                await request.CancelAsync();
                break;
            }
        }

        var run = Assert.Single(conversation.Runs);
        Assert.Equal(RunEnding.Cancelled, run.Ending);

        var finished = Assert.IsType<RunFinishedEvent>(Restatements.Run(conversation, run).Last());
        Assert.IsType<RunFinishedCancelledOutcome>(finished.Outcome);
    }

    private static async Task<(StoredConversation Conversation, RecordingScope Scope)> BeginAsync(FileConversationStore store, string threadId)
    {
        var conversation = await store.GetOrCreateAsync(threadId, Ct);
        var question = new AGUIUserMessage { Id = "u-1", Content = "Why is WO-1001 short-cycling?" };
        var scope = new RecordingScope
        {
            Store = store,
            Lease = conversation.TryBeginRun()!,
            Input = new RunAgentInput { ThreadId = threadId, RunId = "r-1", Messages = [question] },
            Plan = new RunPlan { Branch = [], NewMessages = [question], RestatesBranch = false },
            Run = new RunScope(),
            Restatements = [],
            Time = TimeProvider.System,
        };

        return (conversation, scope);
    }

    // The start of an answer, then either an exception (a broken stream) or text the client never stays for.
    private static async IAsyncEnumerable<BaseEvent> Answer(string threadId, bool thenBreak, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new RunStartedEvent { ThreadId = threadId, RunId = "r-1", ProtocolVersion = "1.0" };
        yield return new TextMessageStartEvent { MessageId = "msg-1", Role = AGUIRoles.Assistant };
        yield return new TextMessageContentEvent { MessageId = "msg-1", Delta = "Let me check" };

        if (thenBreak)
        {
            throw new InvalidOperationException("The pipeline failed mid-stream.");
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
