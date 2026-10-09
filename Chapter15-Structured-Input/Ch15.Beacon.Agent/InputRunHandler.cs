using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AGUI.Hosting;
using Beacon.AI;
using Ch15.Beacon.Agent.Input;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ch15.Beacon.Agent;

/// <summary>
/// Chapter 4's four steps with a gate in front: resume entries are checked against the server's records
/// before anything runs, and the model gets the server's version of each answer, never the client's list.
/// </summary>
public sealed partial class InputRunHandler(
    IChatClient chatClient,
    BackendTools tools,
    InputForms forms,
    InputResumeGate gate,
    IInputRequestStore inputs,
    IOptions<JsonOptions> jsonOptions,
    ILogger<InputRunHandler> logger)
{
    /// <summary>Beacon's instructions, plus when to ask a person rather than guess.</summary>
    public const string Instructions = BeaconInstructions.Default +
        """

        - Some things only the technician on site can tell you. When a tool reports that equipment details are
          missing, ask for them with ask_for_serial_number or ask_for_fan_motor_reading; never guess a serial
          number or a reading. Ask for everything you need in one response, then wait for the answers.
        """;

    public IResult Handle(RunAgentInput input, HttpContext http)
    {
        var run = new InputRun(input.ThreadId, input.RunId);

        // 1. Every resume entry must answer a request this server raised on this thread: open, in time,
        //    valid against the schema it sent, once. And no open request may be left unanswered.
        var (decided, rejection) = gate.Check(input, run);
        if (rejection is not null)
        {
            LogRefused(logger, run.ThreadId, rejection.StatusCode, rejection.Title);
            return rejection.ToResult();
        }

        // 2. Chapter 4's steps on input the server vouches for. The client's resume list never reaches the SDK;
        //    the model sees its own calls and the answers as their results, rebuilt from the records.
        var context = WithoutResume(input).ToChatRequestContext(jsonOptions.Value.SerializerOptions, InputInterrupts.StreamOptions());
        context.Messages.AddRange(InputExchange.Apply(decided, forms));
        context.ChatOptions.Instructions = Instructions;

        IList<AITool> ours = [.. tools.Create(), .. forms.CreateTools()];
        context.ChatOptions.Tools = [.. ours, .. (context.ChatOptions.Tools ?? []).Where(t => !ours.Any(o => o.Name == t.Name))];
        context.ChatOptions.AdditionalProperties![InputRun.Key] = run;

        var updates = chatClient.GetStreamingResponseAsync(context.Messages, context.ChatOptions, http.RequestAborted);
        var events = updates.AsAGUIEventStreamAsync(context, http.RequestAborted);

        // 3. The requests the run raised open as its RUN_FINISHED goes out; any it never delivered are withdrawn.
        return AGUIResults.Events(InputEventStream.RunAsync(events, run, inputs, http.RequestAborted), http, http.RequestAborted);
    }

    private static RunAgentInput WithoutResume(RunAgentInput input) => new()
    {
        ThreadId = input.ThreadId,
        RunId = input.RunId,
        ParentRunId = input.ParentRunId,
        ProtocolVersion = input.ProtocolVersion,
        Messages = input.Messages,
        Tools = input.Tools,
        Context = input.Context,
        State = input.State,
        ForwardedProperties = input.ForwardedProperties,
    };

    // The thread and the reason only: never the payload, which is the person's data.
    [LoggerMessage(EventId = 1501, Level = LogLevel.Warning, Message = "Refused a run on thread {ThreadId} with {StatusCode}: {Title}.")]
    private static partial void LogRefused(ILogger logger, string threadId, int statusCode, string title);
}
