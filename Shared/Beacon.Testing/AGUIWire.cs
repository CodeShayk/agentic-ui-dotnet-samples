using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Formatting;

namespace Beacon.Testing;

/// <summary>Reads AG-UI streams the way a client sees them on the wire.</summary>
public static class AGUIWire
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver,
    };

    /// <summary>POSTs a run and returns every event the server streamed back.</summary>
    public static async Task<IReadOnlyList<BaseEvent>> RunAsync(
        HttpClient http,
        string path,
        RunAgentInput input,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(input, WireOptions.GetTypeInfo(typeof(RunAgentInput))),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SseEventStreamFormatter.ServerSentEventsMediaType));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var events = new List<BaseEvent>();
        await foreach (var evt in new SseEventStreamFormatter().ReadAsync(body, cancellationToken))
        {
            events.Add(evt);
        }

        return events;
    }

    /// <summary>
    /// POSTs a run and returns each SSE <c>data:</c> payload exactly as the server wrote it, parsed but
    /// never re-serialized. Use this to prove what is (and isn't) on the wire, for example that no
    /// optional field is written as <c>null</c>; <see cref="ToWireJson"/> can't, because it round-trips
    /// through the SDK, which drops nulls.
    /// </summary>
    public static async Task<IReadOnlyList<JsonElement>> RunRawAsync(
        HttpClient http,
        string path,
        RunAgentInput input,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(input, WireOptions.GetTypeInfo(typeof(RunAgentInput))),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SseEventStreamFormatter.ServerSentEventsMediaType));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var frames = new List<JsonElement>();
        await foreach (var item in System.Net.ServerSentEvents.SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(item.Data);
            frames.Add(document.RootElement.Clone());
        }

        return frames;
    }

    /// <summary>
    /// Properties whose values are opaque, application-defined JSON. The spec lets them contain
    /// anything, <c>null</c> included, so null checks don't descend into them. (<c>rawEvent</c>, for
    /// example, is the SDK's echo of the source <c>ChatResponseUpdate</c>.)
    /// </summary>
    public static IReadOnlySet<string> OpaqueProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "rawEvent", "snapshot", "delta", "patch", "value", "event", "result", "payload",
        "metadata", "state", "forwardedProps", "parameters", "responseSchema", "content",
    };

    /// <summary>
    /// Finds every AG-UI protocol property whose value is JSON <c>null</c>, as a path such as
    /// <c>$.parentRunId</c>. Opaque payloads (<see cref="OpaqueProperties"/>) are not searched.
    /// </summary>
    public static IReadOnlyList<string> NullProperties(JsonElement element, string path = "$")
    {
        var found = new List<string>();
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = $"{path}.{property.Name}";
                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        found.Add(childPath);
                    }
                    else if (!OpaqueProperties.Contains(property.Name))
                    {
                        found.AddRange(NullProperties(property.Value, childPath));
                    }
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    found.AddRange(NullProperties(item, $"{path}[{index++}]"));
                }

                break;
        }

        return found;
    }

    /// <summary>Creates a minimal run with one user message.</summary>
    public static RunAgentInput UserRun(string text, string threadId = "thread-1", string runId = "run-1") => new()
    {
        ThreadId = threadId,
        RunId = runId,
        Messages = [new AGUIUserMessage { Id = "user-1", Content = text }],
    };

    /// <summary>The event as it appears on the wire, for property-by-property assertions.</summary>
    public static JsonElement ToWireJson(BaseEvent evt) =>
        JsonSerializer.SerializeToElement(evt, WireOptions.GetTypeInfo(typeof(BaseEvent)));

    /// <summary>The wire <c>type</c> of each event, in order.</summary>
    public static IReadOnlyList<string> Types(IEnumerable<BaseEvent> events) =>
        [.. events.Select(e => ToWireJson(e).GetProperty("type").GetString()!)];
}
