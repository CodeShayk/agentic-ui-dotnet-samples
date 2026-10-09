using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AGUI.Hosting;
using Ch20.Beacon.Agent.Surfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ch20.Beacon.Agent;

/// <summary>
/// Chapter 4's steps with a gate in front, as in Chapters 14 and 15: a user action in
/// <c>forwardedProps</c> is checked against the server's records before anything runs, and the model hears
/// about it as the server's account, appended to the conversation.
/// </summary>
public sealed partial class SurfaceRunHandler(
    IChatClient chatClient,
    BeaconToolbox toolbox,
    SurfaceActionGate gate,
    SurfaceLedger ledger,
    IOptions<JsonOptions> jsonOptions,
    ILogger<SurfaceRunHandler> logger)
{
    public IResult Handle(RunAgentInput input, HttpContext http)
    {
        // 1. A user action must answer a surface this server issued on this thread: open, in time, once, valid.
        var (accepted, refusal) = gate.Check(input);
        if (refusal is not null)
        {
            LogRefused(logger, input.ThreadId, refusal.StatusCode, refusal.Title);
            return refusal.ToResult();
        }

        // 2. Chapter 4's steps. MapResult paints each accepted surface right after its render_a2ui result.
        var context = input.ToChatRequestContext(jsonOptions.Value.SerializerOptions, SurfaceEvents.StreamOptions(ledger));
        if (accepted is not null)
        {
            context.Messages.AddRange(accepted.ToMessages());
        }

        toolbox.Configure(context.ChatOptions);

        // 3. Every RUN_STARTED declares the protocol version this agent speaks (Chapter 19).
        var updates = chatClient.GetStreamingResponseAsync(context.Messages, context.ChatOptions, http.RequestAborted);
        var events = updates.AsAGUIEventStreamAsync(context, http.RequestAborted)
            .WithProtocolVersion(AGUIProtocol.Version, http.RequestAborted);
        return AGUIResults.Events(events, http, http.RequestAborted);
    }

    // The thread and the reason only: never the values, which are the customer's data.
    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Refused a run on thread {ThreadId} with {StatusCode}: {Title}.")]
    private static partial void LogRefused(ILogger logger, string threadId, int statusCode, string title);
}
