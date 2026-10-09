using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Beacon.Testing;
using Ch16.Beacon.Desk.Reasoning;

namespace Ch16.Tests.Infrastructure;

/// <summary>Builds runs, sends them and reads what came back, as a client would.</summary>
public static class Runs
{
    public const string Diagnose = "Why is WO-1001 short-cycling?";
    public const string Reading = "Priya measured the run capacitor at 38 µF. What now?";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static RunAgentInput Turn(string threadId, string runId, params AGUIMessage[] messages) => new()
    {
        ThreadId = threadId,
        RunId = runId,
        Messages = [.. messages],
    };

    public static AGUIUserMessage User(string id, string text) => new() { Id = id, Content = text };

    /// <summary>Each raw <c>data:</c> payload, exactly as the server wrote it.</summary>
    public static Task<IReadOnlyList<JsonElement>> RawAsync(HttpClient http, RunAgentInput input) =>
        AGUIWire.RunRawAsync(http, "/agent", input, Ct);

    /// <summary>The wire types in order, with runs of the same type collapsed to one.</summary>
    public static IReadOnlyList<string> Shape(IEnumerable<JsonElement> frames)
    {
        List<string> shape = [];
        foreach (var type in frames.Select(Type))
        {
            if (shape.Count == 0 || shape[^1] != type)
            {
                shape.Add(type);
            }
        }

        return shape;
    }

    public static string Type(JsonElement frame) => frame.GetProperty("type").GetString()!;

    public static IEnumerable<JsonElement> OfType(this IEnumerable<JsonElement> frames, string type) =>
        frames.Where(f => Type(f) == type);

    /// <summary>The <c>data:</c> payloads of a recorded SSE body.</summary>
    public static IReadOnlyList<JsonElement> Frames(string sseBody)
    {
        List<JsonElement> frames = [];
        foreach (var line in sseBody.Split('\n'))
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(line["data: ".Length..]);
                frames.Add(document.RootElement.Clone());
            }
        }

        return frames;
    }
}

/// <summary>
/// Beacon Desk's client stack against the in-memory agent: the SDK's HTTP transport, a tap, AGUIChatClient
/// and AgentConversation, configured as the Desk's Program configures them.
/// </summary>
public sealed class DeskStack : IDisposable
{
    public DeskStack(AgentHost host, bool returnSealedReasoning = true)
    {
        (var http, Recorder) = host.RecordingClient();
        Transport = new RecordingTransport(new AGUIChatClientOptions(http, new Uri(http.BaseAddress!, "/agent").ToString()).Transport);
        Tap = new WireEventTap(Transport);
        Chat = new AGUIChatClient(new AGUIChatClientOptions { Transport = Tap });
        Conversation = new AgentConversation(Chat, new ConversationOptions
        {
            Tap = Tap,
            ConfigureInput = returnSealedReasoning ? ReasoningRoundTrip.ConfigureInput : null,
        });
    }

    public RawHttpRecorder Recorder { get; }

    public RecordingTransport Transport { get; }

    public WireEventTap Tap { get; }

    public AGUIChatClient Chat { get; }

    public AgentConversation Conversation { get; }

    public void Dispose()
    {
        Conversation.Dispose();
        Chat.Dispose();
    }
}

/// <summary>Passes runs through and keeps every input it sent.</summary>
public sealed class RecordingTransport(IAGUITransport inner) : IAGUITransport
{
    private readonly List<RunAgentInput> _inputs = [];

    public IReadOnlyList<RunAgentInput> Inputs
    {
        get
        {
            lock (_inputs)
            {
                return [.. _inputs];
            }
        }
    }

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (_inputs)
        {
            _inputs.Add(input);
        }

        await foreach (var evt in inner.SendAsync(input, cancellationToken))
        {
            yield return evt;
        }
    }
}
