using AGUI.Client;
using Beacon.Domain;
using Ch23.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch23.Tests;

/// <summary>Hardening must not break a well-behaved client: AGUIChatClient, full history every turn.</summary>
public sealed class OfficialClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AGUIChatClient_holds_a_conversation_and_approves_an_order()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);
        using var client = new AGUIChatClient(new AGUIChatClientOptions(http, new Uri(http.BaseAddress!, "/agent").ToString()));
        var options = new ChatOptions(); // reused, so the thread ID stays the same
        List<ChatMessage> history = [];

        // Turn 1: a question answered with a backend tool.
        history.Add(new ChatMessage(ChatRole.User, "What is the status of WO-1001?") { MessageId = "user-1" });
        history.AddRange((await client.GetStreamingResponseAsync(history, options, Ct).ToChatResponseAsync(Ct)).Messages);

        // Turn 2: an order that needs approval. The client sends everything it has.
        history.Add(new ChatMessage(ChatRole.User, "Order CMP-CA36 for WO-1001") { MessageId = "user-2" });
        var proposal = await client.GetStreamingResponseAsync(history, options, Ct).ToChatResponseAsync(Ct);
        history.AddRange(proposal.Messages);
        var request = proposal.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Single();

        // Turn 3: Dana approves.
        history.Add(new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true)]) { MessageId = "user-3" });
        var done = await client.GetStreamingResponseAsync(history, options, Ct).ToChatResponseAsync(Ct);

        Assert.Contains("Order PO-9001 is placed", done.Text, StringComparison.Ordinal);
        Assert.Single(ApprovalEnforcementTests.Store(host).ListPartOrders());

        // Nothing the official client sent was mistaken for a forgery.
        Assert.DoesNotContain(host.Logs, l => l.EventId is 2301 or 2302);
    }

    [Fact]
    public async Task A_long_conversation_past_the_caps_for_new_input_never_locks_itself_out()
    {
        // A run budget high enough for fifty turns in a row; the caps under test are the validator's.
        await using var host = Hosts.Agent(new Dictionary<string, string?> { ["Beacon:Security:RunsPerMinute"] = "1000" });

        // A long site note makes the dispatcher's job list a tool result well over the 4,000-character cap.
        var store = host.Services.GetRequiredService<IFieldServiceStore>();
        var workOrder = store.GetWorkOrder("WO-1003")!;
        store.UpdateWorkOrder(workOrder with
        {
            Notes = [.. workOrder.Notes, string.Concat(Enumerable.Repeat("Access through the loading dock; the boiler room key is with the building manager. ", 40))],
        });

        var http = host.CreateClient(TestTokens.Dana);
        using var client = new AGUIChatClient(new AGUIChatClientOptions(http, new Uri(http.BaseAddress!, "/agent").ToString()));
        var options = new ChatOptions();
        List<ChatMessage> history = [];

        async Task TurnAsync(int turn, string text)
        {
            // AGUIChatClient sends everything, the server's own answers and tool results included. A 400 would throw.
            history.Add(new ChatMessage(ChatRole.User, text) { MessageId = $"user-{turn}" });
            history.AddRange((await client.GetStreamingResponseAsync(history, options, Ct).ToChatResponseAsync(Ct)).Messages);
        }

        await TurnAsync(0, "List the open jobs.");
        var listing = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.True(listing?.Length > 4_000, $"The job list is {listing?.Length} characters.");

        for (var turn = 1; history.Count <= 200; turn++)
        {
            await TurnAsync(turn, "What is the status of WO-1001?");
        }

        // Every turn ran on the server's copy, and nothing the client echoed was mistaken for a forgery.
        Assert.True(history.Count > 200);
        Assert.Contains("WO-1001", history[^1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Logs, l => l.EventId is 2301 or 2302);
    }
}
