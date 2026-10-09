using System.Collections.Concurrent;
using Beacon.AI;
using Beacon.Domain;
using Ch18.Beacon.Agent;
using Ch18.Beacon.Agent.Specialists;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch18.Tests.Infrastructure;

/// <summary>Records what the model was asked (messages and the tools offered), then lets the scripted model answer.</summary>
public sealed class RecordingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    private readonly ConcurrentQueue<(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> Tools)> _calls = new();

    public IReadOnlyList<(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> Tools)> Calls => [.. _calls];

    /// <summary>When set, a call whose options offer this tool throws, as a broken model deployment would.</summary>
    public string? FailWhenOffered { get; set; }

    /// <summary>When set and it returns updates for the tools offered, those are the answer instead of the script's.</summary>
    public Func<IReadOnlyList<string>, IReadOnlyList<ChatResponseUpdate>?>? AnswerWhen { get; set; }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
        _calls.Enqueue(([.. messages], tools));

        if (AnswerWhen?.Invoke(tools) is { } answer)
        {
            return answer.ToAsyncEnumerable();
        }

        return FailWhenOffered is { } tool && tools.Contains(tool)
            ? throw new HttpRequestException("503 Service Unavailable from model-eastus2.contoso.internal")
            : base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}

/// <summary>
/// The in-memory field-service store with hooks on the three lookups the specialists make, so a test can
/// make one of them slow or make them wait for each other.
/// </summary>
public sealed class TestStore : IFieldServiceStore
{
    private readonly InMemoryFieldServiceStore _inner = new();

    public Action? BeforeSearchParts { get; init; }

    public Action? BeforeGetSchedule { get; init; }

    public Action? BeforeGetWarranty { get; init; }

    /// <summary>
    /// Each of the three lookups waits until all three have started. If the coordinator ran its specialists
    /// one after another, the first would wait alone and give up; <paramref name="met"/> records who met.
    /// </summary>
    public static TestStore Rendezvous(ConcurrentBag<bool> met)
    {
        var arrived = new CountdownEvent(3);
        void Meet()
        {
            arrived.Signal();
            met.Add(arrived.Wait(TimeSpan.FromSeconds(10)));
        }

        return new TestStore { BeforeSearchParts = Meet, BeforeGetSchedule = Meet, BeforeGetWarranty = Meet };
    }

    public IReadOnlyList<Part> SearchParts(string query, string? model = null)
    {
        BeforeSearchParts?.Invoke();
        return _inner.SearchParts(query, model);
    }

    public IReadOnlyList<ScheduleSlot> GetSchedule(DateOnly date, string? technicianId = null)
    {
        BeforeGetSchedule?.Invoke();
        return _inner.GetSchedule(date, technicianId);
    }

    public WarrantyStatus? GetWarranty(string equipmentId)
    {
        BeforeGetWarranty?.Invoke();
        return _inner.GetWarranty(equipmentId);
    }

    public WorkOrder? GetWorkOrder(string workOrderId) => _inner.GetWorkOrder(workOrderId);
    public IReadOnlyList<WorkOrder> ListWorkOrders(WorkOrderStatus? status = null, string? technicianId = null) => _inner.ListWorkOrders(status, technicianId);
    public WorkOrder UpdateWorkOrder(WorkOrder workOrder) => _inner.UpdateWorkOrder(workOrder);
    public Customer? GetCustomer(string customerId) => _inner.GetCustomer(customerId);
    public Equipment? GetEquipment(string equipmentId) => _inner.GetEquipment(equipmentId);
    public Equipment UpdateEquipment(Equipment equipment) => _inner.UpdateEquipment(equipment);
    public Part? GetPart(string sku) => _inner.GetPart(sku);
    public IReadOnlyList<FaultRecord> GetFaultHistory(string equipmentId) => _inner.GetFaultHistory(equipmentId);
    public IReadOnlyList<Technician> ListTechnicians() => _inner.ListTechnicians();
    public PartOrder PlacePartOrder(string workOrderId, string sku, int quantity) => _inner.PlacePartOrder(workOrderId, sku, quantity);
    public IReadOnlyList<PartOrder> ListPartOrders(string? workOrderId = null) => _inner.ListPartOrders(workOrderId);
    public CustomerCharge ChargeCustomer(string workOrderId, decimal amount, string description) => _inner.ChargeCustomer(workOrderId, amount, description);
}

/// <summary>
/// Ch18.Beacon.Agent in memory, built exactly as its Program.Main builds it, with the scripted model at full
/// speed, no back-office latency and a short specialist timeout, so the outage costs a second, not eight.
/// </summary>
public sealed class AgentHost(IFieldServiceStore? store = null) : WebApplicationFactory<Program>
{
    public RecordingChatClient Model { get; } = new(new ScriptedChatClient(
        [.. SpecialistScripts.Rules, .. BeaconScripts.Default], new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero }));

    public IFieldServiceStore Store => Services.GetRequiredService<IFieldServiceStore>();

    /// <summary>A client that labels itself the way Beacon Desk does.</summary>
    public HttpClient Client(string clientId = "desk-test")
    {
        var http = CreateClient();
        http.DefaultRequestHeaders.Add("X-Beacon-Client", clientId);
        return http;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Beacon:Specialists:ToolDelayMilliseconds", "0");
        builder.UseSetting("Beacon:Specialists:TimeoutSeconds", "1");
        builder.ConfigureTestServices(services =>
        {
            services.AddKeyedSingleton<IChatClient>(Program.ModelKey, Model);
            if (store is not null)
            {
                services.AddSingleton(store);
            }
        });
    }
}
