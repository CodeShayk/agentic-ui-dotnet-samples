using System.Text.Json;
using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Beacon.Testing;
using Ch09.Beacon.Agent.Tools;
using Ch09.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ch09.Tests;

/// <summary>
/// The properties every backend tool should have, checked on Beacon's: idempotent (a repeat changes
/// nothing, and a replayed history runs nothing again), validated and authorized inside the tool.
/// </summary>
public sealed class ToolDesignTests
{
    [Fact]
    public async Task Setting_a_checklist_item_twice_changes_it_once()
    {
        await using var agent = new AgentHost();

        var first = await ChecklistResultAsync(agent, "Mark C3 done on WO-1001", "r-1");
        var second = await ChecklistResultAsync(agent, "Mark C3 done on WO-1001", "r-2");

        Assert.True(first.GetProperty("changed").GetBoolean());
        Assert.False(second.GetProperty("changed").GetBoolean());
        Assert.All(new[] { first, second }, r =>
        {
            Assert.True(r.GetProperty("done").GetBoolean());
            Assert.Equal(1, r.GetProperty("doneCount").GetInt32());
        });

        var store = agent.Services.GetRequiredService<IFieldServiceStore>();
        Assert.Equal([false, false, true, false], store.GetWorkOrder("WO-1001")!.Checklist.Select(i => i.Done));
    }

    [Fact]
    public async Task Read_only_tools_answer_the_same_way_twice_and_change_nothing()
    {
        await using var agent = new AgentHost();
        var store = agent.Services.GetRequiredService<IFieldServiceStore>();
        var before = Snapshot(store);

        var first = await agent.RunAsync("Prepare WO-1001 for Priya", runId: "r-1");
        var second = await agent.RunAsync("Prepare WO-1001 for Priya", runId: "r-2");

        Assert.Equal(
            first.OfType<ToolCallResultEvent>().Select(r => r.Content.ToString()),
            second.OfType<ToolCallResultEvent>().Select(r => r.Content.ToString()));
        Assert.Equal(before, Snapshot(store));
        Assert.Empty(store.ListPartOrders());
    }

    [Fact]
    public async Task A_replayed_history_never_runs_its_tools_again()
    {
        var store = new CountingStore(new InMemoryFieldServiceStore());
        await using var agent = new AgentHost(services: s => s.Replace(ServiceDescriptor.Singleton<IFieldServiceStore>(store)));

        var first = await agent.RunAsync("What's the status of WO-1001?");
        Assert.Equal(1, store.WorkOrderReads);

        // The client sends the whole conversation back, tool call and result included, plus a new
        // question. The server sees the call already answered and doesn't run it again.
        var call = first.OfType<ToolCallStartEvent>().Single();
        var result = first.OfType<ToolCallResultEvent>().Single();
        var input = new RunAgentInput
        {
            ThreadId = "t-1",
            RunId = "r-2",
            Messages =
            [
                new AGUIUserMessage { Id = "user-1", Content = "What's the status of WO-1001?" },
                new AGUIAssistantMessage
                {
                    Id = call.ParentMessageId!,
                    ToolCalls = [new AGUIToolCall { Id = call.ToolCallId, Function = new AGUIToolCallFunction { Name = call.ToolCallName, Arguments = "{\"workOrderId\":\"WO-1001\"}" } }],
                },
                new AGUIToolMessage { Id = result.MessageId, ToolCallId = result.ToolCallId, Content = result.Content.ToString() },
                new AGUIUserMessage { Id = "user-2", Content = "Thanks, that's all for now." },
            ],
        };

        var second = await AGUIWire.RunAsync(agent.CreateClient(), "/agent", input, TestContext.Current.CancellationToken);

        Assert.Empty(second.OfType<ToolCallStartEvent>());
        Assert.Equal(1, store.WorkOrderReads);
    }

    [Fact]
    public async Task Arguments_with_the_wrong_shape_are_refused_inside_the_tool()
    {
        var tool = new ChecklistTool(new InMemoryFieldServiceStore(), () => new CurrentUser("Dana Kim", UserRole.Dispatcher)).Create();

        var refused = await InvokeAsync(tool, new() { ["workOrderId"] = "WO-1001; DROP TABLE", ["itemId"] = "C3", ["done"] = true });
        var missing = await InvokeAsync(tool, new() { ["workOrderId"] = "WO-1001", ["itemId"] = "C9", ["done"] = true });

        Assert.Equal("Use a work order ID like WO-1001 and a checklist item ID like C3.", refused.GetProperty("error").GetString());
        Assert.Equal("WO-1001 has no checklist item C9.", missing.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Who_may_change_a_checklist_comes_from_the_server_not_the_arguments()
    {
        // Sam (TECH-4) isn't assigned to WO-1001; Priya (TECH-1) is.
        await using var sam = new AgentHost(new Dictionary<string, string?>
        {
            ["Beacon:User:Name"] = "Sam Whitaker",
            ["Beacon:User:Role"] = "Technician",
            ["Beacon:User:TechnicianId"] = "TECH-4",
        });

        var refused = await ChecklistResultAsync(sam, "Mark C3 done on WO-1001", "r-1");
        Assert.Equal("Only the dispatcher or the technician assigned to WO-1001 can change its checklist.", refused.GetProperty("error").GetString());
        Assert.False(sam.Services.GetRequiredService<IFieldServiceStore>().GetWorkOrder("WO-1001")!.Checklist[2].Done);

        await using var priya = new AgentHost(new Dictionary<string, string?>
        {
            ["Beacon:User:Role"] = "Technician",
            ["Beacon:User:TechnicianId"] = "TECH-1",
        });

        var allowed = await ChecklistResultAsync(priya, "Mark C3 done on WO-1001", "r-1");
        Assert.True(allowed.GetProperty("changed").GetBoolean());
    }

    private static async Task<JsonElement> ChecklistResultAsync(AgentHost agent, string text, string runId)
    {
        var events = await agent.RunAsync(text, runId: runId);
        var result = Assert.Single(events.OfType<ToolCallResultEvent>());
        using var document = JsonDocument.Parse(result.Content.ToString());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> InvokeAsync(AIFunction tool, Dictionary<string, object?> arguments)
    {
        var result = await tool.InvokeAsync(new AIFunctionArguments(arguments), TestContext.Current.CancellationToken);
        return Assert.IsType<JsonElement>(result);
    }

    private static string Snapshot(IFieldServiceStore store) =>
        JsonSerializer.Serialize(store.ListWorkOrders(), BeaconJson.Options.GetTypeInfo(typeof(IReadOnlyList<WorkOrder>)));

    /// <summary>The in-memory store, counting work-order reads.</summary>
    private sealed class CountingStore(IFieldServiceStore inner) : IFieldServiceStore
    {
        private int _workOrderReads;

        public int WorkOrderReads => Volatile.Read(ref _workOrderReads);

        public WorkOrder? GetWorkOrder(string workOrderId)
        {
            Interlocked.Increment(ref _workOrderReads);
            return inner.GetWorkOrder(workOrderId);
        }

        public IReadOnlyList<WorkOrder> ListWorkOrders(WorkOrderStatus? status = null, string? technicianId = null) => inner.ListWorkOrders(status, technicianId);

        public WorkOrder UpdateWorkOrder(WorkOrder workOrder) => inner.UpdateWorkOrder(workOrder);

        public Customer? GetCustomer(string customerId) => inner.GetCustomer(customerId);

        public Equipment? GetEquipment(string equipmentId) => inner.GetEquipment(equipmentId);

        public Equipment UpdateEquipment(Equipment equipment) => inner.UpdateEquipment(equipment);

        public IReadOnlyList<Part> SearchParts(string query, string? model = null) => inner.SearchParts(query, model);

        public Part? GetPart(string sku) => inner.GetPart(sku);

        public WarrantyStatus? GetWarranty(string equipmentId) => inner.GetWarranty(equipmentId);

        public IReadOnlyList<FaultRecord> GetFaultHistory(string equipmentId) => inner.GetFaultHistory(equipmentId);

        public IReadOnlyList<Technician> ListTechnicians() => inner.ListTechnicians();

        public IReadOnlyList<ScheduleSlot> GetSchedule(DateOnly date, string? technicianId = null) => inner.GetSchedule(date, technicianId);

        public PartOrder PlacePartOrder(string workOrderId, string sku, int quantity) => inner.PlacePartOrder(workOrderId, sku, quantity);

        public IReadOnlyList<PartOrder> ListPartOrders(string? workOrderId = null) => inner.ListPartOrders(workOrderId);

        public CustomerCharge ChargeCustomer(string workOrderId, decimal amount, string description) => inner.ChargeCustomer(workOrderId, amount, description);
    }
}
