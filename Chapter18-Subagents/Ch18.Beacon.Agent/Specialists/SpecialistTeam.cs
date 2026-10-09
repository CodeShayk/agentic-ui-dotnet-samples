using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch18.Beacon.Agent.Approvals;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ch18.Beacon.Agent.Specialists;

/// <summary>
/// Beacon's three specialists. Each gets its own pipeline over the shared model, its own instructions and
/// only the tools its job needs: the parts specialist can order (behind Chapter 14's spending policy and
/// the check at the tool), the other two can only read.
/// </summary>
public sealed class SpecialistTeam
{
    public const string PartsName = "parts-specialist";
    public const string SchedulingName = "scheduling-specialist";
    public const string WarrantyName = "warranty-specialist";

    public SpecialistTeam(
        [FromKeyedServices(Program.ModelKey)] IChatClient model,
        IFieldServiceStore store,
        SpendingApprovalPolicy policy,
        IApprovalStore approvals,
        IApprovalAudit audit,
        TimeProvider time,
        IOptions<SpecialistOptions> options,
        ILoggerFactory loggers)
    {
        var settings = options.Value;
        var delay = TimeSpan.FromMilliseconds(settings.ToolDelayMilliseconds);
        var tools = new BeaconTools(store);
        var lookups = tools.CreateReadOnlyTools().OfType<AIFunction>().ToDictionary(t => t.Name, t => Slow(t));
        var order = tools.CreateConsequentialTools().Single(t => t.Name == BeaconTools.PlacePartOrderName);

        // One pipeline shape for every specialist: its own function invocation, the spending policy
        // between that and the model (Chapter 14), and logging. Only the parts specialist has a tool it gates.
        IChatClient Pipeline() => new ChatClientBuilder(model)
            .UseFunctionInvocation(loggers, functions => functions.MaximumIterationsPerRequest = 4)
            .Use(inner => new ApprovalPolicyChatClient(inner, policy, approvals))
            .UseLogging(loggers)
            .Build();

        All =
        [
            new Specialist(
                new SubagentInfo { Name = PartsName, Description = "Finds parts that fit and orders them within Contoso's spending policy" },
                "Parts specialist",
                SpecialistInstructions.Parts,
                [lookups[BeaconTools.SearchPartsName], new ApprovalCheckedFunction(Slow(order), policy, approvals, audit, time)],
                Pipeline(),
                settings.Timeout),
            new Specialist(
                new SubagentInfo { Name = SchedulingName, Description = "Finds the earliest free slot for a work order's technician" },
                "Scheduling specialist",
                SpecialistInstructions.Scheduling,
                [lookups[BeaconTools.GetScheduleName], lookups[BeaconTools.GetWorkOrderName]],
                Pipeline(),
                settings.Timeout),
            new Specialist(
                new SubagentInfo { Name = WarrantyName, Description = "Checks what the equipment's warranty covers" },
                "Warranty specialist",
                SpecialistInstructions.Warranty,
                [new WarrantyOutage(lookups[BeaconTools.CheckWarrantyName], settings.WarrantyOutage.ToHashSet(StringComparer.OrdinalIgnoreCase)), lookups[BeaconTools.GetWorkOrderName]],
                Pipeline(),
                settings.Timeout),
        ];

        AIFunction Slow(AIFunction function) => delay > TimeSpan.Zero ? new BackOfficeLatency(function, delay) : function;
    }

    public IReadOnlyList<Specialist> All { get; }

    /// <summary>The coordinator's delegation tools, one per specialist.</summary>
    public IList<AITool> DelegationTools => [.. All.Select(s => s.DelegationTool)];

    public Specialist? ForDelegationTool(string toolName) => All.FirstOrDefault(s => s.DelegationToolName == toolName);

    public Specialist? Named(string name) => All.FirstOrDefault(s => s.Name == name);
}

/// <summary>The instructions each part of Beacon works under. All of them live on the server.</summary>
public static class SpecialistInstructions
{
    public const string Coordinator =
        """
        You are Beacon, the coordinator of Contoso Climate's field-service assistants. You don't look
        anything up yourself: you delegate. For each part of the dispatcher's request, call the matching
        specialist once with a short task that names the work order: ask_parts_specialist for parts and
        orders, ask_scheduling_specialist for technicians and time slots, ask_warranty_specialist for
        coverage. Call every specialist you need in the same turn, so they work in parallel.

        When their reports come back, answer in a short Markdown list with one line per specialist. Say
        plainly when a specialist failed or is waiting for an approval, and never claim an order was
        placed unless the parts specialist's report says so.
        """;

    public const string Parts =
        """
        You are Contoso Climate's parts specialist. Find the part that fits the work order's equipment with
        search_parts. When the task asks you to order it, call place_part_order for one unit. Orders over
        $500 wait for a dispatcher's approval; that is expected, so don't retry. Report in one or two
        sentences: SKU, name, price, stock and what you ordered.
        """;

    public const string Scheduling =
        """
        You are Contoso Climate's scheduling specialist. Use get_schedule (and get_work_order to see who
        is assigned) to find the earliest free slot today for the work order. Report in one sentence: who
        and when.
        """;

    public const string Warranty =
        """
        You are Contoso Climate's warranty specialist. Use check_warranty (and get_work_order to find the
        equipment) and report in one sentence what is covered and until when.
        """;
}
