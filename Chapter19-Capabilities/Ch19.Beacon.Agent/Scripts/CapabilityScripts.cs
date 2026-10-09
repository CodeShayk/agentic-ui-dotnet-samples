using System.Globalization;
using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch19.Beacon.Agent.Scripts;

/// <summary>
/// This chapter's rules for the scripted model, tried before Beacon's defaults. The scripted model is
/// not multimodal: it can confirm that an attachment reached it, by kind and size, and nothing more.
/// A real model that reads images, audio or PDFs would answer from the content.
/// </summary>
public static class CapabilityScripts
{
    /// <summary>Beacon Desk's screen tool (a frontend tool the Desk declares and runs).</summary>
    public const string OpenWorkOrderName = "open_work_order";

    public static IReadOnlyList<ScriptRule> Rules { get; } =
    [
        new("attachments", Reply: ctx =>
            ctx.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Contents.OfType<DataContent>().ToList() is { Count: > 0 } parts
                ? ScriptReply.Say(
                    $"I received {string.Join(" and ", parts.Select(Describe))}. The scripted model can only confirm that " +
                    "it arrived; on a deployment with a model that reads it, I'd tell you what's in it.")
                : null),

        // Calls the Desk's screen tool when the request offers it; otherwise falls through to get_work_order.
        new("open-work-order",
            Reply: ctx =>
                ctx.Mentions("open") && ctx.Find(BeaconScripts.WorkOrderPattern) is { } workOrderId
                    ? ScriptReply.Call(OpenWorkOrderName, new Dictionary<string, object?> { ["workOrderId"] = workOrderId })
                    : null,
            Summarize: ctx =>
                ctx.ToolResults.Any(r => ctx.ToolNameFor(r) == OpenWorkOrderName)
                    ? ScriptReply.Say($"**{ctx.Find(BeaconScripts.WorkOrderPattern)}** is open on your screen.")
                    : null),
    ];

    private static string Describe(DataContent part)
    {
        var kind = part.MediaType switch
        {
            var t when t.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "your photo",
            var t when t.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "your recording",
            "application/pdf" => "your PDF",
            _ => "your file",
        };

        return string.Create(CultureInfo.InvariantCulture, $"{kind} ({part.MediaType}, {part.Data.Length / 1024d:0.0} KB)");
    }
}
