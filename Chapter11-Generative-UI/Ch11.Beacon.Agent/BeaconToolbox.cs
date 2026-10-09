using System.Text.Json;
using Beacon.AI;
using Beacon.Domain;
using Ch11.Beacon.Agent.Components;
using Ch11.Beacon.Components;
using Microsoft.Extensions.AI;

namespace Ch11.Beacon.Agent;

/// <summary>
/// Everything Beacon offers the model in Chapter 11: its instructions, its read-only tools, the part
/// order behind the card's button and one tool per catalog component.
/// </summary>
public sealed class BeaconToolbox
{
    /// <summary>What the model is told about components. Server-owned, like the rest of its instructions.</summary>
    public const string ComponentInstructions =
        """
        You can show answers as components with the show_* tools. Use show_part_card for a single part,
        show_schedule_timeline for a day's schedule and show_fault_history_chart for a unit's fault history;
        answer in text for anything else, including lists of several parts. Fill a component only with values
        you got from Beacon's other tools. After a component is accepted, add one short sentence; if it is
        rejected, fix the props or answer in text.
        """;

    private readonly HashSet<string> _serverToolNames;

    public BeaconToolbox(IFieldServiceStore store, ComponentRules rules, ILoggerFactory loggers)
    {
        var tools = new BeaconTools(store);
        var componentLogger = loggers.CreateLogger<ComponentTool>();

        Tools =
        [
            .. tools.CreateReadOnlyTools(),
            .. tools.CreateConsequentialTools().Where(t => t.Name == BeaconTools.PlacePartOrderName).Select(t => new SpendingLimitedFunction(t, store)),
            new ComponentTool(BeaconComponents.PartCard, rules.PartCard, componentLogger),
            new ComponentTool(BeaconComponents.ScheduleTimeline, rules.ScheduleTimeline, componentLogger),
            new ComponentTool(BeaconComponents.FaultHistoryChart, rules.FaultHistoryChart, componentLogger),
        ];

        _serverToolNames = [.. Tools.Select(t => t.Name)];
    }

    /// <summary>The server's tools, component tools included.</summary>
    public IReadOnlyList<AITool> Tools { get; }

    /// <summary>Sets the server's instructions and tools on every request.</summary>
    /// <remarks>
    /// <c>ToChatRequestContext</c> already put the client's declared tools on <paramref name="options"/>.
    /// They stay, unless one reuses a server tool's name: a client can't replace a component's schema,
    /// or add a component to the catalog, by declaring a tool.
    /// </remarks>
    public void Configure(ChatOptions options)
    {
        options.Instructions = $"{BeaconInstructions.Default}\n{ComponentInstructions}";
        options.Tools = [.. Tools, .. (options.Tools ?? []).Where(t => !_serverToolNames.Contains(t.Name))];
    }
}

/// <summary>
/// <c>place_part_order</c> with Contoso Climate's spending rule applied in the tool: orders over $500 need a
/// dispatcher's approval, and this agent can't ask for one yet (Chapter 14 adds approvals), so it refuses them.
/// </summary>
internal sealed class SpendingLimitedFunction(AIFunction inner, IFieldServiceStore store) : DelegatingAIFunction(inner)
{
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var sku = Text(arguments.TryGetValue("sku", out var s) ? s : null);
        var quantity = int.TryParse(Text(arguments.TryGetValue("quantity", out var q) ? q : null), out var n) ? n : 1;

        if (sku is not null && store.GetPart(sku) is { } part && SpendingPolicy.RequiresApproval(part.UnitPrice * quantity))
        {
            return ValueTask.FromResult<object?>(new ToolError(
                $"An order of {quantity} x {part.Sku} costs ${part.UnitPrice * quantity:0.00}, over the $500 limit; it needs a dispatcher's approval. Nothing was ordered."));
        }

        return base.InvokeCoreAsync(arguments, cancellationToken);
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.GetRawText(),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };
}
