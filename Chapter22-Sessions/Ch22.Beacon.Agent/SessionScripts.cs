using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.AI;
using Ch22.Beacon.Agent.Compaction;
using Ch22.Beacon.Agent.Readings;
using Microsoft.Extensions.AI;

namespace Ch22.Beacon.Agent;

/// <summary>
/// The scripted model's behaviors for this chapter, in front of Beacon's defaults: Elena's C33 diagnosis on
/// WO-1005 (the fault history, then a request for line voltages, then a verdict from the readings), a note for
/// the customer and the summaries compaction asks for. A real model does all of this without scripts and less
/// predictably.
/// </summary>
public static partial class SessionScripts
{
    /// <summary>
    /// Creates the rules. Each set counts the customer notes it has written, so a regenerated note comes out in
    /// the other phrasing; a real model varies on its own.
    /// </summary>
    public static IReadOnlyList<ScriptRule> Create()
    {
        var notes = 0;
        return
        [
            new("compaction-summary", Reply: ctx =>
                ctx.UserText.StartsWith(HistoryCompactor.PromptMarker, StringComparison.Ordinal) ? ScriptReply.Say(Summary(ctx.UserText)) : null),

            new("locked-rotor",
                Reply: ctx => ctx.Mentions("C33", "locked rotor") && ctx.HasTool(BeaconTools.GetFaultHistoryName)
                    ? ScriptReply.Call(BeaconTools.GetFaultHistoryName, new Dictionary<string, object?> { ["equipmentId"] = "EQ-701" },
                        "Let me check EQ-701's fault history first.")
                    : null,
                Summarize: ctx => ctx.Mentions("C33", "locked rotor") && LastToolIs(ctx, BeaconTools.GetFaultHistoryName) && ctx.HasTool(LineVoltageReadings.ToolName)
                    ? new ScriptReply
                    {
                        Text = "EQ-701 logged **C31, compressor current imbalance**, on September 27 and **C33, locked rotor**, today. " +
                               "An unbalanced or missing phase overheats a healthy compressor until it trips on locked rotor, " +
                               "so the supply comes first. I've asked Elena for the line voltages at the contactor.",
                        ToolCalls = [new ScriptToolCall(LineVoltageReadings.ToolName, new Dictionary<string, object?> { ["workOrderId"] = "WO-1005" })],
                    }
                    : null),

            new("voltage-verdict", Summarize: ctx =>
                LastToolIs(ctx, LineVoltageReadings.ToolName) ? ScriptReply.Say(Verdict(ScriptedChatClient.ResultJson(ctx.ToolResults[^1]))) : null),

            new("customer-note", Reply: ctx =>
                ctx.Mentions("note", "email") && ctx.Mentions("Northgate", "facilities", "customer")
                    ? ScriptReply.Say(Note(ctx, Interlocked.Increment(ref notes)))
                    : null),

            .. BeaconScripts.Default,
        ];
    }

    private static bool LastToolIs(ScriptContext ctx, string toolName) =>
        ctx.ToolResults.Count > 0 && ctx.ToolNameFor(ctx.ToolResults[^1]) == toolName;

    /// <summary>The voltage imbalance by the NEMA method: the largest deviation from the average, as a percentage of it.</summary>
    public static (decimal Average, decimal Imbalance, string LowLeg, decimal Low)? Imbalance(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("l1l2", out _))
        {
            return null;
        }

        (string Leg, decimal Volts)[] legs = [("L1-L2", root.GetProperty("l1l2").GetDecimal()), ("L2-L3", root.GetProperty("l2l3").GetDecimal()), ("L1-L3", root.GetProperty("l1l3").GetDecimal())];
        var average = legs.Average(l => l.Volts);
        var worst = legs.MaxBy(l => Math.Abs(l.Volts - average));
        var low = legs.MinBy(l => l.Volts);
        return (average, average == 0 ? 0 : Math.Abs(worst.Volts - average) / average * 100m, low.Leg, low.Volts);
    }

    private static string Verdict(string json)
    {
        if (Imbalance(json) is not { } reading)
        {
            return "Without the readings I can't rule out the supply. Before anyone condemns the compressor, someone needs to measure the three line voltages at the contactor.";
        }

        var (average, imbalance, lowLeg, low) = reading;
        var text = new StringBuilder();
        if (low < average / 2)
        {
            text.Append(CultureInfo.InvariantCulture, $"{lowLeg} reads {low:0} V: the unit is losing a phase. ")
                .Append("Single-phasing stalls a compressor and trips C33 every time. Check the fuses and the contactor before replacing anything; the compressor may be fine.");
        }
        else if (imbalance > 2m)
        {
            text.Append(CultureInfo.InvariantCulture, $"The supply is **{imbalance:0.0}% unbalanced** ({lowLeg} is low at {low:0} V against a {average:0} V average). ")
                .Append("Compressor makers allow about 2%; past that the windings overheat and the compressor trips on locked rotor. ")
                .Append(CultureInfo.InvariantCulture, $"Have the connections and fuses on {lowLeg.Replace("-", " and ", StringComparison.Ordinal)} checked, and Northgate's electrician look at the feed, before anyone condemns the compressor.");
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture, $"The supply is balanced ({imbalance:0.0}%), so it isn't the cause. ")
                .Append("With C33 repeating, the compressor is the likely culprit: confirm with a winding resistance check, then check the warranty and order CMP-RT120.");
        }

        return text.ToString();
    }

    private static string Note(ScriptContext ctx, int attempt)
    {
        var readings = ctx.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Where(r => ctx.ToolNameFor(r) == LineVoltageReadings.ToolName)
            .Select(r => Imbalance(ScriptedChatClient.ResultJson(r)))
            .LastOrDefault(r => r is not null);

        // After compaction the readings may survive only in the summary, which reaches the model as a labeled message.
        var summary = ctx.Messages.FirstOrDefault(m => m.Role == ChatRole.User && m.Text.StartsWith(HistoryCompactor.SummaryLabel, StringComparison.Ordinal));
        var imbalance = readings?.Imbalance.ToString("0.0", CultureInfo.InvariantCulture)
            ?? (SummarizedImbalance().Match(summary?.Text ?? string.Empty) is { Success: true } m ? m.Groups[1].Value : null);

        var finding = imbalance is not null
            ? $"the power reaching the unit is {imbalance}% unbalanced across its three phases (about 2% is the limit)"
            : "the compressor alarm points to the electrical supply";

        return attempt % 2 == 1
            ? "**Subject:** WO-1005, rooftop unit RT-120\n\n" +
              "Hello,\n\n" +
              $"Elena Ortiz checked your rooftop unit this morning. The compressor keeps shutting down because {finding}. " +
              "Please have your electrician inspect the supply to the unit; replacing the compressor wouldn't fix it.\n\n" +
              "We'll schedule a follow-up visit once the supply is checked.\n\nContoso Climate dispatch"
            : "**Subject:** Your rooftop unit (WO-1005): next step is electrical\n\n" +
              "Hello,\n\n" +
              "Quick update from Contoso Climate:\n\n" +
              $"- **What we found:** {char.ToUpperInvariant(finding[0])}{finding[1..]}.\n" +
              "- **What it means:** the compressor trips to protect itself. It isn't the part to replace.\n" +
              "- **What we need from you:** an electrician's check of the feed to the unit.\n\n" +
              "Reply to this note and we'll book the return visit.\n\nContoso Climate dispatch";
    }

    // A deterministic stand-in for a model's summary: one bullet per question, lookup, reading and the last answer.
    private static string Summary(string prompt)
    {
        var lines = prompt.ReplaceLineEndings("\n").Split('\n');
        var earlierAt = Array.IndexOf(lines, "Earlier summary:");
        var transcriptAt = Array.IndexOf(lines, "Transcript:");

        List<string> bullets = [.. lines[(earlierAt + 1)..transcriptAt].Where(l => l.StartsWith("- ", StringComparison.Ordinal))];
        string? lastAnswer = null;

        foreach (var line in lines[(transcriptAt + 1)..])
        {
            if (line.StartsWith("user: ", StringComparison.Ordinal))
            {
                bullets.Add($"- Asked: {Clip(line[6..], 90)}");
            }
            else if (line.StartsWith("assistant -> ", StringComparison.Ordinal))
            {
                var call = line[13..];
                bullets.Add($"- Looked up {Clip(call, 80)}");
            }
            else if (line.StartsWith($"tool {LineVoltageReadings.ToolName}: ", StringComparison.Ordinal)
                     && Imbalance(line[(LineVoltageReadings.ToolName.Length + 7)..]) is { } r)
            {
                bullets.Add(string.Create(CultureInfo.InvariantCulture, $"- Readings: {r.Imbalance:0.0}% voltage imbalance, {r.LowLeg} lowest at {r.Low:0} V."));
            }
            else if (line.StartsWith("assistant: ", StringComparison.Ordinal))
            {
                lastAnswer = line[11..];
            }
        }

        if (lastAnswer is not null)
        {
            bullets.Add($"- Last answer: {Clip(FirstSentence(lastAnswer), 140)}");
        }

        return string.Join('\n', bullets.TakeLast(8));
    }

    private static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? text : text[..(end + 1)];
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    [GeneratedRegex(@"Readings: (\d+\.\d)% voltage imbalance")]
    private static partial Regex SummarizedImbalance();
}
