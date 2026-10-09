using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using AGUI.Abstractions;

namespace Ch03.EventTour;

public sealed class TourOptions
{
    public const string SectionName = "Tour";

    /// <summary>The pause between events, so a stream visibly streams in a terminal. Tests use 0.</summary>
    public int EventDelayMilliseconds { get; set; } = 40;
}

/// <summary>One scripted run: the words that select it, a prompt that does, and the events it emits.</summary>
/// <param name="Script">
/// Yields <see cref="BaseEvent"/>s, or raw JSON strings for frames no conforming producer would send
/// (only the "broken" scenario does that).
/// </param>
public sealed record TourScenario(string Name, string Prompt, string[] Keywords, Func<TourRun, IEnumerable<object>> Script);

public static class Tour
{
    // The AG-UI resolver gives the protocol's names and omits unset fields. The relaxed encoder keeps
    // quotes and apostrophes inside strings readable (\" rather than \u0022); both parse identically,
    // and the payload is read as JSON by clients, never embedded in HTML.
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The scenarios, tried in order; the first whose keyword appears in the user's message wins.</summary>
    public static IReadOnlyList<TourScenario> Scenarios { get; } =
    [
        new("broken", "Show me a broken stream", ["broken"], Scripts.Broken),
        new("error", "Check the parts inventory, then fail", ["fail"], Scripts.Error),
        new("cancelled", "Draft the WO-1001 report, then stop", ["stop"], Scripts.Cancelled),
        new("interrupt", "Order a CMP-CA36 compressor for WO-1001", ["order"], Scripts.Interrupt),
        new("history", "Restore the WO-1001 conversation", ["restore"], Scripts.History),
        new("subagents", "Ask the specialists about EQ-501", ["specialist"], Scripts.Subagents),
        new("reasoning", "Why is EQ-501 short-cycling?", ["why"], Scripts.Reasoning),
        new("state", "Start the checklist for WO-1001", ["checklist"], Scripts.State),
        new("activity", "Search for a capacitor for EQ-501", ["search"], Scripts.Activity),
        new("passthrough", "Notify Priya that CAP-45-5 is in stock", ["notify"], Scripts.Passthrough),
        new("chunks", "Brief me on WO-1001", ["brief"], Scripts.Chunks),
        new("tools", "What is the status of WO-1001?", ["status"], Scripts.Tools),
        new("text", "Hello, Beacon", [], Scripts.Text),
    ];

    public static TourScenario Choose(RunAgentInput input)
    {
        var text = input.Messages?.OfType<AGUIUserMessage>().LastOrDefault() is { } user ? TextOf(user.Content) : string.Empty;

        return Scenarios.FirstOrDefault(s => s.Keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
            ?? Scenarios[^1];
    }

    /// <summary>Plays a scenario as the JSON payloads of Server-Sent Events, one event per frame.</summary>
    public static async IAsyncEnumerable<string> PlayAsync(
        TourScenario scenario,
        RunAgentInput input,
        TourOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var run = new TourRun(input.ThreadId, input.RunId);

        foreach (var item in scenario.Script(run))
        {
            if (options.EventDelayMilliseconds > 0)
            {
                await Task.Delay(options.EventDelayMilliseconds, cancellationToken);
            }

            yield return item switch
            {
                BaseEvent evt => JsonSerializer.Serialize(evt, WireOptions.GetTypeInfo(typeof(BaseEvent))),
                string frame => frame,
                _ => throw new InvalidOperationException($"A scenario yielded {item.GetType().Name}."),
            };
        }
    }

    private static string TextOf(AGUIContent content) =>
        content.IsText
            ? (string)content.Value
            : string.Join(' ', content.OfType<AGUITextInputContent>().Select(part => part.Text));
}

/// <summary>Identifiers and helpers for one scripted run.</summary>
public sealed class TourRun(string threadId, string runId)
{
    private readonly string _key = new([.. runId.Where(char.IsLetterOrDigit)]);

    public string ThreadId { get; } = threadId;

    public string RunId { get; } = runId;

    /// <summary>A message, tool-call or span id that is unique within the thread: <c>msg-r1-1</c> for run <c>r-1</c>.</summary>
    public string Id(string prefix, int number = 1) => $"{prefix}-{_key}-{number}";

    public RunStartedEvent Started() => new()
    {
        ThreadId = ThreadId,
        RunId = RunId,
        ProtocolVersion = AGUIProtocol.Version,
    };

    public RunFinishedEvent Finished(RunFinishedOutcome? outcome = null, IList<TokenUsage>? usage = null) => new()
    {
        ThreadId = ThreadId,
        RunId = RunId,
        Outcome = outcome!,
        Usage = usage!,
    };

    /// <summary>A whole text message: start, a content event per few words, end.</summary>
    public static IEnumerable<BaseEvent> Say(string messageId, string text, string? subagentRunId = null)
    {
        yield return new TextMessageStartEvent { MessageId = messageId, Role = AGUIRoles.Assistant, SubagentRunId = subagentRunId! };

        foreach (var delta in Chunk(text))
        {
            yield return new TextMessageContentEvent { MessageId = messageId, Delta = delta };
        }

        yield return new TextMessageEndEvent { MessageId = messageId };
    }

    /// <summary>Splits text into deltas of three words, the way a model streams tokens.</summary>
    public static IEnumerable<string> Chunk(string text, int wordsPerDelta = 3)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i += wordsPerDelta)
        {
            var delta = string.Join(' ', words.Skip(i).Take(wordsPerDelta));
            yield return i + wordsPerDelta < words.Length ? delta + " " : delta;
        }
    }

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
