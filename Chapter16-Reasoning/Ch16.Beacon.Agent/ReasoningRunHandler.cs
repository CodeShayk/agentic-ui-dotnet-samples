using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AGUI.Hosting;
using Beacon.AI;
using Ch16.Beacon.Agent.Reasoning;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ch16.Beacon.Agent;

/// <summary>
/// Chapter 4's four steps with the reasoning carried around the SDK: reasoning messages come out of the
/// input before <c>ToChatRequestContext</c> (which can't convert them) and their sealed values go back
/// into the history afterward, where the model produced them.
/// </summary>
public sealed partial class ReasoningRunHandler(
    IChatClient chatClient,
    BeaconTools tools,
    IOptions<JsonOptions> jsonOptions,
    ILogger<ReasoningRunHandler> logger)
{
    public IResult Handle(RunAgentInput input, HttpContext http)
    {
        // 1. Reasoning out. The client's reasoning text stays behind; only the provider's sealed values go on.
        var (rest, carried, error) = ReasoningHistory.Split(input);
        if (error is not null)
        {
            LogRefused(logger, input.ThreadId, error);
            return TypedResults.Problem(title: "The request carries an invalid reasoning message.", detail: error, statusCode: StatusCodes.Status400BadRequest);
        }

        // 2. Chapter 4's adaptation, on everything else.
        var context = rest.ToChatRequestContext(jsonOptions.Value.SerializerOptions);

        // 3. The sealed values back in, as the provider's own content.
        ReasoningHistory.Restore(context.Messages, carried);

        // The server's instructions and tools. Clients get no say in either.
        context.ChatOptions.Instructions = BeaconInstructions.Default;
        context.ChatOptions.Tools = [.. tools.CreateReadOnlyTools()];

        // 4. Run the model and stream the result.
        var updates = chatClient.GetStreamingResponseAsync(context.Messages, context.ChatOptions, http.RequestAborted);
        var events = updates.AsAGUIEventStreamAsync(context, http.RequestAborted);
        return AGUIResults.Events(events, http, http.RequestAborted);
    }

    [LoggerMessage(EventId = 1602, Level = LogLevel.Warning, Message = "Refused a run on thread {ThreadId}: {Reason}")]
    private static partial void LogRefused(ILogger logger, string threadId, string reason);
}
