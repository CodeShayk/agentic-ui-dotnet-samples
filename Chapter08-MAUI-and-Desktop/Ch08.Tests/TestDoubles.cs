using System.Net;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.AI;
using Beacon.Domain;
using Ch08.Beacon.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ch08.Tests;

/// <summary>Beacon's agent pipeline in-process: the scripted model, the read-only tools and place_part_order.</summary>
public static class InProcessAgent
{
    public static IChatClient Create(IFieldServiceStore store) =>
        new ChatClientBuilder(new ScriptedChatClient(BeaconScripts.Default, new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero }))
            .ConfigureOptions(options =>
            {
                options.Instructions = BeaconInstructions.Default;
                options.Tools = [.. new BeaconTools(store).CreateReadOnlyTools(), new PartOrdering(store, NullLogger<PartOrdering>.Instance).CreateTool()];
            })
            .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
            .Build();
}

/// <summary>
/// The network between the client and the agent, with faults: refuse the first attempts before any
/// byte arrives, or drop the connection after a given event. A drop lets the agent produce one more
/// event (so a tool call's work happens) and loses it, the way a phone loses the bytes in flight.
/// </summary>
public sealed class FlakyNetwork(IAGUITransport inner) : IAGUITransport
{
    private int _refusalsLeft;

    /// <summary>Every input sent, one per attempt.</summary>
    public List<RunAgentInput> Attempts { get; } = [];

    /// <summary>Refuse this many attempts before letting one through (as a connection error).</summary>
    public int RefuseFirst
    {
        get => _refusalsLeft;
        init => _refusalsLeft = value;
    }

    /// <summary>Drop the connection after the first event this matches, on the first run only.</summary>
    public Func<BaseEvent, bool>? DropAfter { get; set; }

    /// <summary>
    /// On a drop, let the agent finish its whole run first and lose all of it, the way a reset discards
    /// everything a slow app hadn't read yet. Otherwise the agent gets one more event.
    /// </summary>
    public bool AgentFinishesBeforeDrop { get; init; }

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Attempts.Add(input);
        if (_refusalsLeft > 0)
        {
            _refusalsLeft--;
            throw new HttpRequestException(HttpRequestError.ConnectionError, "No route to host (simulated).");
        }

        var dropAfter = DropAfter;
        await using var events = inner.SendAsync(input, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (await events.MoveNextAsync())
        {
            yield return events.Current;

            if (dropAfter?.Invoke(events.Current) == true)
            {
                DropAfter = null;
                while (await events.MoveNextAsync() && AgentFinishesBeforeDrop)
                {
                    // the agent carries on, tools and all...
                }

                throw new IOException("Unable to read data from the transport connection (simulated)."); // ...which never arrives
            }
        }
    }
}

/// <summary>A transport that plays one scripted behavior per attempt, for unit tests of the retry layer.</summary>
public sealed class ScriptedTransport(params Func<RunAgentInput, CancellationToken, IAsyncEnumerable<BaseEvent>>[] attempts) : IAGUITransport
{
    private int _next;

    public List<RunAgentInput> Inputs { get; } = [];

    public IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, CancellationToken cancellationToken)
    {
        Inputs.Add(input);
        return _next < attempts.Length
            ? attempts[_next++](input, cancellationToken)
            : throw new InvalidOperationException($"No scripted behavior for attempt {Inputs.Count}.");
    }
}

/// <summary>Behaviors for <see cref="ScriptedTransport"/>.</summary>
public static class Attempt
{
    public static Func<RunAgentInput, CancellationToken, IAsyncEnumerable<BaseEvent>> Fails(Exception exception) =>
        (_, _) => Throwing(exception);

    public static Func<RunAgentInput, CancellationToken, IAsyncEnumerable<BaseEvent>> Answers(string text, Exception? breakAfter = null, int keep = int.MaxValue) =>
        (input, ct) => Streaming(Run(input, text).Take(keep), breakAfter, holdOpen: null, ct);

    /// <summary>Starts a run, streams a little and then waits until it's cancelled, like a slow model.</summary>
    public static Func<RunAgentInput, CancellationToken, IAsyncEnumerable<BaseEvent>> Holds(string text, TaskCompletionSource holding) =>
        (input, ct) => Streaming(Run(input, text).Take(3), breakAfter: null, holding, ct);

    public static IEnumerable<BaseEvent> Run(RunAgentInput input, string text)
    {
        yield return new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId };
        yield return new TextMessageStartEvent { MessageId = "msg-1", Role = AGUIRoles.Assistant };
        yield return new TextMessageContentEvent { MessageId = "msg-1", Delta = text };
        yield return new TextMessageEndEvent { MessageId = "msg-1" };
        yield return new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId };
    }

    public static HttpRequestException Status(HttpStatusCode status) =>
        new($"Response status code does not indicate success: {(int)status}.", inner: null, status);

    private static async IAsyncEnumerable<BaseEvent> Throwing(Exception exception)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162 // an async iterator needs a yield to be one
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<BaseEvent> Streaming(IEnumerable<BaseEvent> events, Exception? breakAfter, TaskCompletionSource? holdOpen, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var evt in events)
        {
            await Task.Yield();
            yield return evt;
        }

        if (breakAfter is not null)
        {
            throw breakAfter;
        }

        if (holdOpen is not null)
        {
            holdOpen.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }
}
