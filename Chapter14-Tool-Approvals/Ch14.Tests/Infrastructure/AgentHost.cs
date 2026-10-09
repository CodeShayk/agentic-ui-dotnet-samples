using System.Collections.Concurrent;
using Beacon.AI;
using Beacon.Domain;
using Ch14.Beacon.Agent;
using Ch14.Beacon.Agent.Approvals;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch14.Tests.Infrastructure;

/// <summary>A clock the tests move forward, for expiry.</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 16, 14, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Records what the model was asked, then lets the scripted model answer.</summary>
public sealed class RecordingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    private readonly ConcurrentQueue<IReadOnlyList<ChatMessage>> _calls = new();

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Calls => [.. _calls];

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue([.. messages]);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}

/// <summary>
/// Ch14.Beacon.Agent in memory, built exactly as its Program.Main builds it, with the test model, a manual
/// clock, and clients that label themselves the way Beacon Desk does.
/// </summary>
public sealed class AgentHost : WebApplicationFactory<Program>
{
    public RecordingChatClient Model { get; } = new(new ScriptedChatClient(TestModel.Rules, new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero }));

    public ManualClock Clock { get; } = new();

    public IFieldServiceStore Store => Services.GetRequiredService<IFieldServiceStore>();

    public IApprovalStore Approvals => Services.GetRequiredService<IApprovalStore>();

    public IReadOnlyList<ApprovalAuditEntry> Audit => Services.GetRequiredService<IApprovalAudit>().Entries;

    /// <summary>A client that sends <paramref name="clientId"/> as its <c>X-Beacon-Client</c> label.</summary>
    public HttpClient Client(string clientId = "desk-test")
    {
        var http = CreateClient();
        http.DefaultRequestHeaders.Add(BeaconClient.HeaderName, clientId);
        return http;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddKeyedSingleton<IChatClient>(Program.ModelKey, Model);
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

/// <summary>The scripted model plus a behavior no well-behaved model should have.</summary>
public static class TestModel
{
    public const string ShowWorkOrderTool = "show_work_order";

    /// <summary>"Show WO-1001 and order a replacement compressor": a frontend call and a purchase in one turn.</summary>
    public static ScriptRule ShowAndOrder { get; } = new("show-and-order", Reply: ctx =>
        ctx.Mentions("show") && ctx.Mentions("compressor") && ctx.Find(BeaconScripts.WorkOrderPattern) is { } workOrderId
            ? new ScriptReply
            {
                ToolCalls =
                [
                    new ScriptToolCall(ShowWorkOrderTool, new Dictionary<string, object?> { ["workOrderId"] = workOrderId }),
                    new ScriptToolCall(BeaconTools.PlacePartOrderName,
                        new Dictionary<string, object?> { ["workOrderId"] = workOrderId, ["sku"] = "CMP-CA36", ["quantity"] = 1 }),
                ],
            }
            : null);

    public static IReadOnlyList<ScriptRule> Rules { get; } = [ShowAndOrder, .. ApprovalScripts.Rules, .. BeaconScripts.Default];
}
