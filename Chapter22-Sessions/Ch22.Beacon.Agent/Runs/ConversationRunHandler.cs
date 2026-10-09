using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AGUI.Hosting;
using Beacon.AI;
using Ch22.Beacon.Agent.Compaction;
using Ch22.Beacon.Agent.Conversations;
using Ch22.Beacon.Agent.Readings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ch22.Beacon.Agent.Runs;

/// <summary>
/// Chapter 4's four steps around a store: find the thread, plan the run against the server's copy of the
/// branch, fit the history to the model's budget, then run, restate and record.
/// </summary>
public sealed partial class ConversationRunHandler(
    IChatClient chatClient,
    IConversationStore store,
    RunPlanner planner,
    HistoryCompactor compactor,
    LineVoltageReadings readings,
    BeaconTools tools,
    IOptions<JsonOptions> jsonOptions,
    TimeProvider time,
    ILogger<ConversationRunHandler> logger)
{
    /// <summary>Beacon's instructions, plus when to ask the technician rather than guess.</summary>
    public const string Instructions = BeaconInstructions.Default +
        """

        - Some facts only the technician on site can supply. When a diagnosis needs the line voltages at a
          compressor, ask with ask_for_line_voltages and wait for the readings; never guess a reading.
        """;

    public async Task<IResult> HandleAsync(RunAgentInput input, HttpContext http)
    {
        var cancellationToken = http.RequestAborted;

        // 1. IDs become store keys: present, well formed, unique.
        if (RunPlanner.Validate(input) is { Count: > 0 } errors)
        {
            return Results.ValidationProblem(errors, title: "The run input is not valid.");
        }

        // 2. One run at a time on a thread. The lease is released when the run is recorded, or when the
        //    response ends if the stream never started.
        var conversation = await store.GetOrCreateAsync(input.ThreadId, cancellationToken);
        var lease = conversation.TryBeginRun();
        if (lease is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Run in progress",
                detail: $"Thread {input.ThreadId} is already running. Wait for its terminal event before sending the next run.");
        }

        http.Response.OnCompleted(() =>
        {
            lease.Dispose();
            return Task.CompletedTask;
        });

        try
        {
            // 3. The server's copy is the history; the client's copy shows where it is and what's new.
            var (plan, rejection) = planner.Plan(conversation, input);
            if (rejection is not null)
            {
                lease.Dispose();
                LogRefused(logger, input.ThreadId, rejection.StatusCode, rejection.Title);
                return rejection.ToResult();
            }

            // 4. The model sees a summary of old turns and the recent ones verbatim; the client sees everything.
            var history = await compactor.PrepareAsync(conversation, plan!, input.RunId, cancellationToken);
            if (history.Created is { } compaction)
            {
                await store.SaveCompactionAsync(conversation, compaction, cancellationToken);
            }

            // 5. Chapter 4's steps on the server's history. parentRunId names the run this one continues, as
            //    the server resolved it, so RUN_STARTED reports the lineage the store records.
            var modelInput = new RunAgentInput
            {
                ThreadId = input.ThreadId,
                RunId = input.RunId,
                ParentRunId = plan!.Base?.RunId,
                ProtocolVersion = input.ProtocolVersion,
                Messages = [.. history.Messages],
                Tools = input.Tools,
                Context = input.Context,
                State = input.State,
                ForwardedProperties = input.ForwardedProperties,
            };

            var context = modelInput.ToChatRequestContext(jsonOptions.Value.SerializerOptions);

            // The sealed reasoning the store holds for this branch goes back where the model produced it. Reasoning
            // messages from the client were never planned in: the server's copy is the authoritative one.
            SealedReasoning.Restore(context.Messages, [.. plan.Branch, .. plan.NewMessages]);

            // The server's instructions carry only what the server wrote. A summary was written from old turns,
            // tool output included, so it travels as a labeled user message in place of the turns it replaces.
            context.ChatOptions.Instructions = Instructions;
            if (history.Summary is { } summary)
            {
                context.ChatOptions.Instructions += $"\n{HistoryCompactor.SummaryRule}";
                context.Messages.Insert(0, HistoryCompactor.SummaryMessage(summary));
            }

            IList<AITool> ours = [.. CompactTools.Create(tools), readings.Declaration];
            context.ChatOptions.Tools = [.. ours, .. (context.ChatOptions.Tools ?? []).Where(t => !ours.Any(o => o.Name == t.Name))];

            var run = new RunScope();
            context.ChatOptions.AdditionalProperties![RunScope.Key] = run;

            var updates = chatClient.GetStreamingResponseAsync(context.Messages, context.ChatOptions, cancellationToken);
            var events = updates.AsAGUIEventStreamAsync(context, cancellationToken)
                .WithProtocolVersion(AGUIProtocol.Version); // Chapter 19's stamp: AGUI.Server leaves it out

            // 6. Restate the branch when the client may be showing something else, then record as it streams.
            List<BaseEvent> restatements = [];
            if (plan.RestatesBranch)
            {
                restatements.Add(Restatements.Branch([.. plan.Branch, .. plan.NewMessages]));
            }

            if (history.Created is { } created)
            {
                restatements.Add(Restatements.Compaction(created));
            }

            var scope = new RecordingScope
            {
                Store = store,
                Lease = lease,
                Input = input,
                Plan = plan,
                Run = run,
                Restatements = restatements,
                Time = time,
            };

            return AGUIResults.Events(RecordingEventStream.RunAsync(events, scope, cancellationToken), http, cancellationToken);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    [LoggerMessage(EventId = 2231, Level = LogLevel.Warning, Message = "Refused a run on thread {ThreadId} with {StatusCode}: {Title}.")]
    private static partial void LogRefused(ILogger logger, string threadId, int statusCode, string title);
}
