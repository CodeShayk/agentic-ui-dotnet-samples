using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.AI;
using Beacon.Domain;
using Ch21.Beacon.Agent.Interop;
using Microsoft.Extensions.AI;

namespace Ch21.Beacon.Agent;

/// <summary>
/// The scripted model's rules for this chapter, tried before <see cref="BeaconScripts.Default"/>.
/// "manual" or "procedure" plus an equipment model searches the manual, then reads the best
/// section; "supplier", "Northwind" or "lead time" plus a SKU asks Northwind's agent. Both can
/// happen in one turn. Like every scripted rule, this is keyword matching, not judgment: it quotes
/// a section's numbered steps and never reads the rest, which a real model would.
/// </summary>
public static partial class InteropScripts
{
    public const string SearchManual = "search_manual";
    public const string GetManualSection = "get_manual_section";

    private static readonly string[] Topics =
    [
        "locked rotor", "capacitor", "short cycling", "serial number", "nameplate", "grinding", "fan motor",
        "defrost", "fault codes", "igniter", "inspection", "amperage", "warranty claim", "compressor replacement",
    ];

    public static IReadOnlyList<ScriptRule> Rules { get; } =
    [
        new("manuals-and-supplier", Reply: Delegate, Summarize: Summarize),
    ];

    private static ScriptReply? Delegate(ScriptContext ctx)
    {
        List<ScriptToolCall> calls = [];

        if (ctx.Mentions("manual", "procedure") && ModelFor(ctx) is { } model)
        {
            if (!ctx.HasTool(SearchManual))
            {
                return ScriptReply.Say(
                    "I can't reach the equipment manuals right now, so I won't guess at the procedure. " +
                    "Try again in a few minutes, or use the printed manual inside the unit's service panel.");
            }

            calls.Add(new(SearchManual, new Dictionary<string, object?> { ["model"] = model, ["query"] = Topic(ctx) }));
        }

        if (ctx.Mentions("supplier", "northwind", "lead time") && ctx.Find(BeaconScripts.SkuPattern) is { } sku
            && ctx.HasTool(SupplierAgentTool.Name))
        {
            calls.Add(new(SupplierAgentTool.Name, new Dictionary<string, object?> { ["sku"] = sku, ["quantity"] = 1 }));
        }

        return calls.Count > 0 ? new ScriptReply { ToolCalls = calls } : null;
    }

    private static ScriptReply? Summarize(ScriptContext ctx)
    {
        var search = ctx.ToolResults.FirstOrDefault(r => ctx.ToolNameFor(r) == SearchManual);
        var section = ctx.ToolResults.FirstOrDefault(r => ctx.ToolNameFor(r) == GetManualSection);
        var stock = SupplierResultThisTurn(ctx);

        if (search is null && section is null && stock is null)
        {
            return null;
        }

        // After a search, read the best-matching section before answering.
        if (search is not null && section is null && ctx.HasTool(GetManualSection)
            && Content(search) is { } hits && SectionId().Match(hits) is { Success: true } id)
        {
            return ScriptReply.Call(GetManualSection, new Dictionary<string, object?> { ["sectionId"] = id.Value });
        }

        List<string> paragraphs = [];
        if (section is not null)
        {
            paragraphs.Add(DescribeSection(section));
        }
        else if (search is not null)
        {
            paragraphs.Add(DescribeSearch(search));
        }

        if (stock is not null)
        {
            paragraphs.Add(DescribeStock(stock));
        }

        return ScriptReply.Say(string.Join("\n\n", paragraphs));
    }

    private static string DescribeSection(FunctionResultContent result)
    {
        var json = Parse(result);
        if (json.TryGetProperty("error", out var error))
        {
            return $"I couldn't read that manual section: {error.GetString()}";
        }

        var lines = json.GetProperty("content").GetString()!.Split('\n');
        var manual = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..] ?? "the manual";
        var heading = lines.FirstOrDefault(l => l.StartsWith("## ", StringComparison.Ordinal))?[3..] ?? "";
        var (sectionId, title) = heading.Split(' ', 2) is [var first, var rest] ? (first, rest) : (heading, "");

        // Numbered steps and their indented continuation lines.
        List<string> steps = [];
        var inStep = false;
        foreach (var line in lines)
        {
            inStep = Step().IsMatch(line) || (inStep && line.StartsWith("   ", StringComparison.Ordinal));
            if (inStep)
            {
                steps.Add(line);
            }
        }

        // A section without numbered steps is quoted whole.
        if (steps.Count == 0)
        {
            steps = [.. lines.SkipWhile(l => !l.StartsWith("## ", StringComparison.Ordinal)).Skip(1).SkipWhile(string.IsNullOrWhiteSpace)];
        }

        var text = $"Here's what the {manual} says in section {sectionId} ({title}):\n\n{string.Join('\n', steps)}";

        var removed = json.GetProperty("removedPassages").GetInt32();
        if (removed > 0)
        {
            text += $"\n\nBeacon removed {removed} passage(s) addressed to AI systems from this section, so treat its field notes with caution.";
        }

        return text;
    }

    private static string DescribeSearch(FunctionResultContent result) =>
        Parse(result).TryGetProperty("error", out var error)
            ? $"I couldn't search the manual: {error.GetString()}"
            : Content(result) ?? "The manual search returned nothing.";

    private static string DescribeStock(FunctionResultContent result)
    {
        var json = Parse(result);
        if (json.TryGetProperty("error", out var error))
        {
            return $"{error.GetString()} Try again in a few minutes, or call Northwind's order desk.";
        }

        var sku = json.GetProperty("sku").GetString();
        var available = json.GetProperty("quantityAvailable").GetInt32();
        var lead = json.TryGetProperty("leadTimeBusinessDays", out var l) ? l.GetInt32() : 0;
        var from = json.TryGetProperty("shipsFrom", out var f) ? f.GetString() : "their warehouse";

        return !json.GetProperty("carried").GetBoolean()
            ? $"Northwind Supply doesn't carry {sku}."
            : json.GetProperty("canSupply").GetBoolean()
                ? string.Create(CultureInfo.InvariantCulture,
                    $"Northwind Supply can ship {json.GetProperty("quantityRequested").GetInt32()} x {sku} from {from} in {lead} business day(s); they have {available} in stock.")
                : string.Create(CultureInfo.InvariantCulture,
                    $"Northwind Supply has {available} x {sku} on hand; restocking takes {lead} business day(s).");
    }

    // The supplier's answer may have arrived a model call earlier, alongside a manual search.
    private static FunctionResultContent? SupplierResultThisTurn(ScriptContext ctx)
    {
        for (var i = ctx.Messages.Count - 1; i >= 0 && ctx.Messages[i].Role != ChatRole.User; i--)
        {
            if (ctx.Messages[i].Contents.OfType<FunctionResultContent>()
                    .FirstOrDefault(r => ctx.ToolNameFor(r) == SupplierAgentTool.Name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string? ModelFor(ScriptContext ctx) =>
        ctx.Find(@"\b(CA-36X|HP-24S|RT-120|FX-90)\b")
        ?? (ctx.Find(BeaconScripts.WorkOrderPattern) is { } wo
            && SeedData.WorkOrders.FirstOrDefault(w => w.Id == wo) is { } order
            ? SeedData.Equipment.FirstOrDefault(e => e.Id == order.EquipmentId)?.Model
            : null);

    private static string Topic(ScriptContext ctx) =>
        Topics.FirstOrDefault(t => ctx.Mentions(t)) ?? "troubleshooting";

    private static JsonElement Parse(FunctionResultContent result) =>
        JsonDocument.Parse(ScriptedChatClient.ResultJson(result)).RootElement;

    private static string? Content(FunctionResultContent result) =>
        Parse(result).TryGetProperty("content", out var content) ? content.GetString() : null;

    [GeneratedRegex(@"\b(CA-36X|HP-24S|RT-120|FX-90)/\d+\.\d+\b")]
    private static partial Regex SectionId();

    [GeneratedRegex(@"^\d+\.\s")]
    private static partial Regex Step();
}
