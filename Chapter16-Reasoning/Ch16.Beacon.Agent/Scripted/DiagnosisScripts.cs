using System.Globalization;
using System.Text.RegularExpressions;
using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch16.Beacon.Agent.Scripted;

/// <summary>
/// The scripted model's behaviors for this chapter, tried before <see cref="BeaconScripts.Default"/>. Here the
/// scripted model plays a provider that keeps its chain of thought private: it shows a summary (or the whole
/// chain, or nothing, as <see cref="ChatOptions.Reasoning"/> asks), returns the full chain sealed, and on the
/// next turn reads back only what it sealed. A real model decides for itself; the scripts make WO-1001's
/// diagnosis and its follow-up repeatable.
/// </summary>
public sealed partial class DiagnosisScripts(ScriptedReasoningSeal seal)
{
    private int _calls;

    public IReadOnlyList<ScriptRule> Rules =>
    [
        new("capacitor-reading", Reply: FollowUp),
        new("diagnose-wo-1001", Reply: Diagnose),
        new("diagnose-after-history", Summarize: AfterHistory),
    ];

    // "Why is WO-1001 short-cycling?": think, then read the fault history.
    private ScriptReply? Diagnose(ScriptContext ctx) =>
        ctx.Mentions("why", "diagnose", "cause") && ctx.Find(BeaconScripts.WorkOrderPattern) == "WO-1001" && ctx.HasTool(BeaconTools.GetFaultHistoryName)
            ? Think(ctx, DiagnosisText.Summary1, DiagnosisText.Chain1, new FunctionCallContent(
                $"call-history-{Interlocked.Increment(ref _calls):D3}",
                BeaconTools.GetFaultHistoryName,
                new Dictionary<string, object?> { ["equipmentId"] = "EQ-501" }))
            : null;

    // The history is back: think again, then answer.
    private ScriptReply? AfterHistory(ScriptContext ctx) =>
        ctx.ToolResults.Any(r => ctx.ToolNameFor(r) == BeaconTools.GetFaultHistoryName && ScriptedChatClient.ResultJson(r).Contains("E3", StringComparison.Ordinal))
        && ctx.Find(BeaconScripts.WorkOrderPattern) == "WO-1001"
            ? Think(ctx, DiagnosisText.Summary2, DiagnosisText.Chain2, new TextContent(DiagnosisText.Answer2))
            : null;

    // "Priya measured the run capacitor at 38 µF": what the answer can build on depends on what came back sealed.
    private ScriptReply? FollowUp(ScriptContext ctx)
    {
        if (Capacitance().Match(ctx.UserText) is not { Success: true } match)
        {
            return null;
        }

        var reading = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

        // Like a provider, the model reads its own artifacts and nothing else, and refuses a request carrying
        // one that fails verification.
        List<string> remembered = [];
        foreach (var value in ctx.Messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().Select(r => r.ProtectedData).OfType<string>())
        {
            remembered.Add(seal.Open(value)
                ?? throw new InvalidOperationException("A reasoning artifact in the request failed verification."));
        }

        var (summary, chain, answer) = remembered.Any(chain => chain.Contains("42.3", StringComparison.Ordinal))
            ? DiagnosisText.WithEarlierDiagnosis(reading)
            : DiagnosisText.WithoutEarlierDiagnosis(reading);
        return Think(ctx, summary, chain, new TextContent(answer));
    }

    private ScriptReply Think(ScriptContext ctx, string summary, string chain, AIContent then) => new()
    {
        // As much as the request asked to see: the whole chain, a summary or nothing.
        Reasoning = ctx.Options?.Reasoning?.Output switch
        {
            ReasoningOutput.Full => chain,
            ReasoningOutput.None => null,
            _ => summary,
        },

        // ScriptedChatClient emits Reasoning, Text, tool calls and then ExtraContents. A provider hands over
        // its sealed chain at the end of its reasoning, before it answers or calls a tool, so everything after
        // the reasoning goes in ExtraContents, in that order.
        ExtraContents =
        [
            new TextReasoningContent(null) { ProtectedData = seal.Seal(chain) },
            then,
            new UsageContent(Usage(ctx, chain, then)),
        ],
    };

    // The scripted model's token counts are estimates (four characters a token). The point is the shape:
    // reasoning tokens are part of the output count, whether or not anyone saw the reasoning.
    private static UsageDetails Usage(ScriptContext ctx, string chain, AIContent then)
    {
        var input = 900 + Tokens(string.Concat(ctx.Messages.Select(m => m.Text)));
        var reasoning = Tokens(chain);
        var output = reasoning + (then is TextContent text ? Tokens(text.Text) : 20);
        return new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            ReasoningTokenCount = reasoning,
            TotalTokenCount = input + output,
        };
    }

    private static long Tokens(string text) => (text.Length + 3) / 4;

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(?:µF|uF|mfd|microfarads?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Capacitance();
}
