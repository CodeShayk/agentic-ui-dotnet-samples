using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AGUI.Hosting;
using Ch26.Beacon.Agent.State;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ch26.Beacon.Agent.Pipeline;

/// <summary>
/// Chapter 4's four steps, with two stages between the converter and the wire: the protocol version
/// on <c>RUN_STARTED</c> (Chapter 19, from Beacon.AGUI.Hosting) and the work order the run opened, settled as state.
/// </summary>
public sealed class BeaconRunHandler(IChatClient agent, IOptions<JsonOptions> jsonOptions, IOptionsMonitor<RegressionSwitches> regressions)
{
    public IResult Handle(RunAgentInput input, HttpContext http)
    {
        var cancellationToken = http.RequestAborted;

        // 1. Adapt the request. Activity and reasoning messages are valid AG-UI input that
        //    ToChatRequestContext 1.0.0 can't convert (Appendix A); Beacon's model doesn't need them.
        var context = WithoutDisplayOnlyMessages(input).ToChatRequestContext(jsonOptions.Value.SerializerOptions);

        // 2. Run the agent.
        var updates = agent.GetStreamingResponseAsync(context.Messages, context.ChatOptions, cancellationToken);

        // 3. Convert, then stamp the version and settle the shared state.
        var events = updates.AsAGUIEventStreamAsync(context, cancellationToken)
            .WithProtocolVersion(AGUIProtocol.Version, cancellationToken)
            .SettleWorkOrderState(regressions.CurrentValue, cancellationToken);

        // 4. Write SSE (or protobuf, when the client asks and it's registered).
        return AGUIResults.Events(events, http, cancellationToken);
    }

    private static RunAgentInput WithoutDisplayOnlyMessages(RunAgentInput input) =>
        input.Messages.Any(m => m is AGUIActivityMessage or AGUIReasoningMessage)
            ? new RunAgentInput
            {
                ThreadId = input.ThreadId,
                RunId = input.RunId,
                ParentRunId = input.ParentRunId,
                ProtocolVersion = input.ProtocolVersion,
                Messages = [.. input.Messages.Where(m => m is not (AGUIActivityMessage or AGUIReasoningMessage))],
                Tools = input.Tools,
                Context = input.Context,
                State = input.State,
                ForwardedProperties = input.ForwardedProperties,
                Resume = input.Resume,
            }
            : input;
}
