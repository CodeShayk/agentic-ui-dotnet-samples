using System.Text.Json;

namespace Ch03.StreamValidator;

/// <summary>
/// What the validator knows about the request that produced the stream. Everything is optional:
/// without it the validator checks the stream on its own terms; with it, it can also check that the
/// stream answers the request (the thread, the requested run, the frontend tools).
/// </summary>
public sealed record StreamValidatorOptions
{
    /// <summary>The <c>threadId</c> the request named. Every run on the stream must carry it.</summary>
    public string? ThreadId { get; init; }

    /// <summary>The <c>runId</c> the request named. The requested run is the last run on the stream.</summary>
    public string? RunId { get; init; }

    /// <summary>Tools the request advertised. The producer must not answer calls to them itself.</summary>
    public IReadOnlySet<string> FrontendTools { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Message and tool-call ids from the request's history, which a stream may refer back to.</summary>
    public IReadOnlySet<string> KnownEntityIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Reads the options from the <c>RunAgentInput</c> JSON the client sent.</summary>
    public static StreamValidatorOptions FromRunAgentInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A RunAgentInput is a JSON object.", nameof(input));
        }

        var tools = new HashSet<string>(StringComparer.Ordinal);
        if (input.TryGetProperty("tools", out var toolList) && toolList.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in toolList.EnumerateArray())
            {
                if (Json.String(tool, "name") is { } name)
                {
                    tools.Add(name);
                }
            }
        }

        var entities = new HashSet<string>(StringComparer.Ordinal);
        if (input.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                Json.CollectEntityIds(message, entities);
            }
        }

        return new StreamValidatorOptions
        {
            ThreadId = Json.String(input, "threadId"),
            RunId = Json.String(input, "runId"),
            FrontendTools = tools,
            KnownEntityIds = entities,
        };
    }
}
