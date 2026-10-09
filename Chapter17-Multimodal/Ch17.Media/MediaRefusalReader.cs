using System.Text.Json;

namespace Ch17.Media;

/// <summary>A media part the agent set aside, as a client reads it from the stream.</summary>
public sealed record RefusedPart(string MessageId, int Part, string Code, string Reason);

/// <summary>
/// Reads the value of Beacon's <c>beacon.media_refused</c> <c>CUSTOM</c> event. A <c>CUSTOM</c> value is
/// application data that arrived over the wire, so it's read defensively: anything that doesn't have
/// the expected shape is skipped rather than trusted.
/// </summary>
public static class MediaRefusalReader
{
    public const string EventName = "beacon.media_refused";

    public static IEnumerable<RefusedPart> Read(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } report
            || !report.TryGetProperty("refused", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("messageId", out var id) && id.ValueKind == JsonValueKind.String
                && item.TryGetProperty("part", out var part) && part.ValueKind == JsonValueKind.Number && part.TryGetInt32(out var index)
                && item.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                var code = item.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "refused";
                yield return new RefusedPart(id.GetString()!, index, code, reason.GetString()!);
            }
        }
    }
}
