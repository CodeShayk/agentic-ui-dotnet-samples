using System.Net;
using System.Text.Json;
using Beacon.Client.Core;
using Beacon.Domain;
using Ch08.Field.Core;
using Ch08.Field.Core.Connection;
using Ch08.Field.Core.Presentation;
using Ch08.Field.Core.Recovery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ch08.Tests;

/// <summary>
/// Section 8.8's proof against the real agent: Ch08.Beacon.Agent behind WebApplicationFactory with its
/// development faults switched on, and the client stack Beacon Field runs. Whatever the network does,
/// the client never orders CAP-45-5 twice.
/// </summary>
public sealed class DroppedConnectionTests
{
    private static readonly FieldClientOptions Options = new() { Connection = ConnectionPolicy.Default with { BaseDelay = TimeSpan.Zero } };

    [Fact]
    public async Task A_run_refused_with_503_is_retried_after_retry_after_with_the_same_run_id_and_orders_once()
    {
        await using var agent = Agent(("Faults:FirstAttempt", "Unavailable"));
        var requests = new RunIdRecorder();
        using var field = Connect(agent, requests);
        List<ConnectionEvent> events = [];
        field.Transport.ConnectionChanged += (_, e) => events.Add(e);

        await field.Conversation.SendAsync("Order CAP-45-5 for WO-1001.", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ConversationStatus.Idle, field.Conversation.View.Status);
        Assert.Equal(2, requests.RunIds.Count);
        Assert.Single(requests.RunIds.Distinct()); // the retry carried the same run ID
        Assert.Equal([ConnectionStage.Retrying, ConnectionStage.Started, ConnectionStage.Finished], events.Select(e => e.Stage));
        Assert.Equal(TimeSpan.FromSeconds(1), events[0].RetryDelay); // the agent's Retry-After: 1, no sooner
        Assert.Single(Store(agent).ListPartOrders("WO-1001"));
        Assert.Equal("Order PO-9001 is placed: 1 x CAP-45-5 for $38.50.", field.Conversation.View.Messages.OfType<AssistantMessageView>().Last().Text);
    }

    [Fact]
    public async Task A_connection_reset_after_the_request_was_sent_is_not_retried_and_the_field_says_it_cannot_tell()
    {
        await using var agent = Agent(("Faults:FirstAttempt", "Abort"));
        var requests = new RunIdRecorder();
        using var field = Connect(agent, requests);

        await field.Conversation.SendAsync("Order CAP-45-5 for WO-1001.", cancellationToken: TestContext.Current.CancellationToken);

        // The agent dropped it before running anything, but the client can't know that: no retry, no promise.
        var view = field.Conversation.View;
        Assert.Single(requests.RunIds);
        Assert.Empty(Store(agent).ListPartOrders("WO-1001"));
        Assert.Equal((ConversationStatus.Failed, false), (view.Status, view.Failure!.RunNeverStarted));
        var advice = TurnRecovery.Assess(view, runsBeforeTurn: 0, ToolSafety.Beacon);
        Assert.Equal((RecoveryKind.Uncertain, "What is the status of WO-1001?"), (advice.Kind, advice.FollowUp));
    }

    [Fact]
    public async Task A_connection_dropped_after_the_order_was_placed_is_not_retried_and_the_field_says_to_check()
    {
        await using var agent = Agent(("Faults:DropAfterTool", "place_part_order"));
        var requests = new RunIdRecorder();
        using var field = Connect(agent, requests);
        using var chat = new ChatViewModel(field.Conversation, InlineDispatcher.Instance, new ChatViewModelOptions { Transport = field.Transport });

        await chat.SendAsync("Order CAP-45-5 for WO-1001.");

        // One request, one order, a truncated run and advice to check rather than repeat.
        Assert.Single(requests.RunIds);
        Assert.Single(Store(agent).ListPartOrders("WO-1001"));
        Assert.Equal(FailureKind.StreamEnded, field.Conversation.View.Failure!.Kind);
        Assert.Equal(RecoveryKind.CheckFirst, chat.Recovery.Kind);
        Assert.Equal("Ordering CAP-45-5 for WO-1001: cut off before Beacon heard back.", chat.Messages.Single(m => m.HasActivity).Activity);

        // The technician taps "Check WO-1001": a new run that only reads.
        Assert.True(chat.RecoveryCommand.CanExecute(null));
        await chat.SendAsync(chat.Recovery.FollowUp!);

        Assert.Equal(2, requests.RunIds.Distinct().Count());
        Assert.Contains("**WO-1001** is AwaitingParts", chat.Messages.Last().Text);
        Assert.Single(Store(agent).ListPartOrders("WO-1001"));
        Assert.Equal(RecoveryKind.None, chat.Recovery.Kind);
    }

    // Kestrel on a random port, not TestServer: a dropped connection has to be a real socket closing.
    // (TestServer reports HttpContext.Abort() to the client as a cancellation, which no network does.)
    private static WebApplicationFactory<Ch08.Beacon.Agent.Program> Agent(params (string Key, string Value)[] settings)
    {
        var agent = new WebApplicationFactory<Ch08.Beacon.Agent.Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("AI:ScriptedChunkDelayMilliseconds", "0");
            host.UseSetting("Beacon:ToolDelayMilliseconds", "0");
            foreach (var (key, value) in settings)
            {
                host.UseSetting(key, value);
            }
        });

        // Not UseKestrel(0): a factory made by WithWebHostBuilder leaves applying that port to its parent,
        // which never received it, so Kestrel would fall back to localhost:5000. A listen option is
        // applied by this factory itself.
        agent.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        agent.StartServer();
        return agent;
    }

    private static FieldClient Connect(WebApplicationFactory<Ch08.Beacon.Agent.Program> agent, RunIdRecorder requests)
    {
        // The handlers Beacon Field uses, under a recorder that notes each request's run ID.
        requests.InnerHandler = new RetryAfterHandler { InnerHandler = FieldHttp.CreateHandler(TimeSpan.FromSeconds(5)) };
        var http = new HttpClient(requests) { Timeout = Timeout.InfiniteTimeSpan };
        var baseAddress = agent.CreateClient().BaseAddress!;
        return FieldClient.Create(http, new Uri(baseAddress, "agent"), Options);
    }

    private static IFieldServiceStore Store(WebApplicationFactory<Ch08.Beacon.Agent.Program> agent) =>
        agent.Services.GetRequiredService<IFieldServiceStore>();

    /// <summary>Records the run ID of every request that leaves the client.</summary>
    private sealed class RunIdRecorder : DelegatingHandler
    {
        public List<string> RunIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            lock (RunIds)
            {
                RunIds.Add(body.RootElement.GetProperty("runId").GetString()!);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
