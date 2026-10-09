using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Beacon.Domain;
using Beacon.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ch15.Tests;

/// <summary>Chapter 15's agent in-process, on a fake clock the tests (and the client) share.</summary>
public sealed class AgentHost : IAsyncDisposable
{
    /// <summary>Monday, November 16, 2026, 11:20 in Dover (UTC-5): Sam is at Maple Grove Apartments.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 16, 16, 20, 0, TimeSpan.Zero);

    public const string Question =
        "Is the heat pump on WO-1003 still under warranty? Its outdoor fan is grinding, and Sam needs to know whether to replace the motor or just the bearings.";

    private readonly WebApplicationFactory<Ch15.Beacon.Agent.Program> _factory;

    public AgentHost()
    {
        _factory = new WebApplicationFactory<Ch15.Beacon.Agent.Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("AI:ScriptedChunkDelayMilliseconds", "0");
            host.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<IFieldServiceStore>(Store);
            });
        });
    }

    public FakeTimeProvider Clock { get; } = new(Start);

    public CountingStore Store { get; } = new(new InMemoryFieldServiceStore());

    public HttpClient CreateHttpClient() => _factory.CreateClient();

    /// <summary>transport → tap → recorder → AGUIChatClient → AgentConversation, on the agent's fake clock unless given another.</summary>
    public (AgentConversation Conversation, RecordingTransport Transport) CreateConversation(TimeProvider? clientClock = null)
    {
        var tap = WireEventTap.ForHttp(_factory.CreateClient(), "/agent");
        var recorder = new RecordingTransport(tap);
        var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = recorder });
        return (new AgentConversation(client, new ConversationOptions { Tap = tap, TimeProvider = clientClock ?? Clock }), recorder);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

/// <summary>Raw requests and frames, for asserting on exactly what crosses the wire.</summary>
public static class Wire
{
    private static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver };

    public static RunAgentInput FirstRun(string threadId = "t-15") => AGUIWire.UserRun(AgentHost.Question, threadId, "r-1");

    /// <summary>A resuming run on the same thread: the question again (the agent keeps no history) and a resume list.</summary>
    public static RunAgentInput Resume(string runId, params AGUIResume[] entries) => new()
    {
        ThreadId = "t-15",
        RunId = runId,
        Messages = [new AGUIUserMessage { Id = "user-1", Content = AgentHost.Question }],
        Resume = entries,
    };

    public static AGUIResume Answer(string interruptId, string payloadJson) =>
        new() { InterruptId = interruptId, Status = ResumeStatus.Resolved, Payload = Json(payloadJson) };

    public static AGUIResume Abandon(string interruptId) => new() { InterruptId = interruptId, Status = ResumeStatus.Cancelled };

    public const string SerialAnswer = """{"serialNumber":"HP24S-2207-1184","readFrom":"nameplate","nameplatePhotoTaken":true}""";

    public const string ReadingAnswer = """{"amps":2.4,"noise":"grinding","shaftPlay":true}""";

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>POSTs a run and returns the response, whatever its status.</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, RunAgentInput input, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = JsonContent.Create(input, Options.GetTypeInfo(typeof(RunAgentInput))),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await http.SendAsync(request, cancellationToken);
    }

    /// <summary>The first run's interrupts, from its RUN_FINISHED frame: serial number first, then the fan reading.</summary>
    public static async Task<(IReadOnlyList<JsonElement> Frames, string SerialId, string ReadingId)> AskAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var frames = await AGUIWire.RunRawAsync(http, "/agent", FirstRun(), cancellationToken);
        var interrupts = frames[^1].GetProperty("outcome").GetProperty("interrupts");
        return (frames, interrupts[0].GetProperty("id").GetString()!, interrupts[1].GetProperty("id").GetString()!);
    }

    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return Json(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}

/// <summary>Records every run a client makes through it: the input it sent and the events it got back.</summary>
public sealed class RecordingTransport(IAGUITransport inner) : IAGUITransport
{
    public List<(RunAgentInput Input, List<BaseEvent> Events)> Runs { get; } = [];

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<BaseEvent> events = [];
        Runs.Add((input, events));
        await foreach (var evt in inner.SendAsync(input, cancellationToken))
        {
            events.Add(evt);
            yield return evt;
        }
    }
}

/// <summary>The field-service store, counting equipment updates so a test can prove an answer was applied once.</summary>
public sealed class CountingStore(IFieldServiceStore inner) : IFieldServiceStore
{
    public int EquipmentUpdates { get; private set; }

    public Equipment UpdateEquipment(Equipment equipment)
    {
        EquipmentUpdates++;
        return inner.UpdateEquipment(equipment);
    }

    public WorkOrder? GetWorkOrder(string workOrderId) => inner.GetWorkOrder(workOrderId);
    public IReadOnlyList<WorkOrder> ListWorkOrders(WorkOrderStatus? status = null, string? technicianId = null) => inner.ListWorkOrders(status, technicianId);
    public WorkOrder UpdateWorkOrder(WorkOrder workOrder) => inner.UpdateWorkOrder(workOrder);
    public Customer? GetCustomer(string customerId) => inner.GetCustomer(customerId);
    public Equipment? GetEquipment(string equipmentId) => inner.GetEquipment(equipmentId);
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
