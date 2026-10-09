using System.Net.Http.Json;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Formatting;
using AGUI.Protobuf;
using Beacon.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ch24.Tests;

/// <summary>
/// Hosts Ch24.Beacon.Agent exactly as its <c>Program.Main</c> builds it, in memory, on the scripted
/// model with no pause between chunks unless a test asks for one.
/// </summary>
public sealed class AgentHost(params (string Key, string? Value)[] settings)
    : WebApplicationFactory<global::Ch24.Beacon.Agent.Program>
{
    public const string SseType = SseEventStreamFormatter.ServerSentEventsMediaType;
    public const string ProtobufType = ProtobufEventStreamFormatter.ProtobufMediaType;

    private static readonly JsonSerializerOptions WireOptions = new() { TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("AI:Provider", "Scripted");
        builder.UseSetting("AI:ScriptedChunkDelayMilliseconds", "0");
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }

    /// <summary>A run with one user message and fresh IDs unless given.</summary>
    public static RunAgentInput Run(string text = "What is the status of WO-1001?", string? runId = null) =>
        AGUIWire.UserRun(text, threadId: "thread-24", runId: runId ?? $"run-{Guid.NewGuid():N}");

    /// <summary>POSTs a run with the given Accept header (none when null) and returns once the headers arrive.</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, RunAgentInput input, string? accept, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = JsonContent.Create(input, WireOptions.GetTypeInfo(typeof(RunAgentInput))),
        };

        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>Decodes a response body with the formatter its Content-Type names.</summary>
    public static async Task<List<BaseEvent>> ReadEventsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        IAGUIEventStreamFormatter formatter = response.Content.Headers.ContentType?.MediaType == ProtobufType
            ? new ProtobufEventStreamFormatter()
            : new SseEventStreamFormatter();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        List<BaseEvent> events = [];
        await foreach (var evt in formatter.ReadAsync(body, cancellationToken))
        {
            events.Add(evt);
        }

        return events;
    }

    /// <summary>
    /// The events as wire JSON with IDs replaced by their order of first appearance and timestamps
    /// (inside <c>rawEvent</c>) blanked, so two runs of the same script compare equal whatever IDs and
    /// times the model, the tool loop and the converter chose.
    /// </summary>
    public static List<JsonElement> Normalized(IEnumerable<BaseEvent> events)
    {
        Dictionary<string, string> ids = [];
        return [.. events.Select(evt => Normalize(AGUIWire.ToWireJson(evt), ids))];
    }

    private static readonly HashSet<string> IdProperties = ["messageId", "toolCallId", "parentMessageId", "runId", "callId", "responseId"];

    private static JsonElement Normalize(JsonElement element, Dictionary<string, string> ids)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(element, writer, ids);
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer, Dictionary<string, string> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Name == "createdAt" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteStringValue("(time)");
                    }
                    else if (IdProperties.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var id = property.Value.GetString()!;
                        if (!ids.TryGetValue(id, out var canonical))
                        {
                            canonical = $"id-{ids.Count + 1}";
                            ids[id] = canonical;
                        }

                        writer.WriteStringValue(canonical);
                    }
                    else
                    {
                        Write(property.Value, writer, ids);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(item, writer, ids);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>An HttpClient for this host that negotiates like a Beacon client, through AGUIEventStreamHandler.</summary>
    public HttpClient CreateNegotiatingClient(params IAGUIEventStreamFormatter[] formatters) =>
        CreateDefaultClient(new AGUI.Client.AGUIEventStreamHandler(formatters));
}
