using System.Net;
using System.Text.RegularExpressions;
using AGUI.Abstractions;
using Ch27.Beacon.Agent.Security.History;
using Ch27.Beacon.Desk.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;

namespace Ch27.Tests;

/// <summary>Beacon Desk refuses strangers, and calls the agent as whoever signed in.</summary>
public sealed partial class DeskTests
{
    private const string Question = "What is the status of WO-1001?";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_Desk_sends_an_anonymous_visitor_to_sign_in_and_keeps_its_probes_open()
    {
        await using var desk = new DeskHost(new Uri("http://127.0.0.1:9"));
        var http = desk.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var page = await http.GetAsync("/", Ct);
        using var hub = await http.PostAsync("/_blazor/negotiate?negotiateVersion=1", null, Ct);
        using var health = await http.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/dev/sign-in", page.Headers.Location!.AbsolutePath);
        Assert.NotEqual(HttpStatusCode.OK, hub.StatusCode); // no circuit without a user either
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Outside_development_the_Desk_refuses_to_start_without_Entra_settings()
    {
        await using var desk = new DeskHost(new Uri("http://127.0.0.1:9"), environment: "Production");

        var refused = Assert.ThrowsAny<Exception>(() => desk.CreateClient());

        Assert.Contains("Beacon Desk has no sign-in configured", refused.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_development_sign_in_puts_Dana_on_every_page()
    {
        await using var desk = new DeskHost(new Uri("http://127.0.0.1:9"));
        var http = desk.CreateClient(); // follows redirects and keeps cookies

        var form = await http.GetStringAsync("/dev/sign-in", Ct);
        Dictionary<string, string> fields = HiddenInput().Matches(form).ToDictionary(m => m.Groups["name"].Value, m => WebUtility.HtmlDecode(m.Groups["value"].Value));
        fields["Who"] = "dana";
        using var signedIn = await http.PostAsync("/dev/sign-in", new FormUrlEncodedContent(fields), Ct);
        var home = await http.GetStringAsync("/", Ct);

        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Contains("Dana Kim · Dispatcher", WebUtility.HtmlDecode(home), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Desk_calls_the_agent_as_the_signed_in_user()
    {
        await using var agent = AgentHost.StartOnKestrel();
        await using var desk = new DeskHost(agent.Address);

        var (circuit, chat) = desk.OpenCircuit(DevelopmentUsers.Find("dana")!);
        await using (circuit)
        {
            await AskAsync(chat, Question, "desk-thread");
        }

        // The agent filed the conversation under Dana, the user the Desk signed in, not under the Desk.
        var danas = await agent.Store.FindAsync("dana", "desk-thread", Ct);
        Assert.Contains(danas!.Messages.OfType<AGUIUserMessage>(), m => m.Content.ToString() == Question);
        Assert.Single(Directory.EnumerateDirectories(agent.Conversations));
    }

    [Fact]
    public async Task Chapter_23s_isolation_holds_through_the_Desk()
    {
        await using var agent = AgentHost.StartOnKestrel();
        await using var desk = new DeskHost(agent.Address);

        var (danasCircuit, danasChat) = desk.OpenCircuit(DevelopmentUsers.Find("dana")!);
        var (priyasCircuit, priyasChat) = desk.OpenCircuit(DevelopmentUsers.Find("priya")!);
        await using (danasCircuit)
        await using (priyasCircuit)
        {
            await AskAsync(danasChat, Question, "same-thread");

            // Priya uses the same thread ID through the same Desk, and asks about Elena's work order.
            await AskAsync(priyasChat, "What is the status of WO-1005?", "same-thread");
        }

        // Her conversation is her own, and her tools see only her work (Chapter 23's caller-scoped tools).
        var priyas = await agent.Store.FindAsync("priya", "same-thread", Ct);
        Assert.DoesNotContain(priyas!.Messages.OfType<AGUIUserMessage>(), m => m.Content.ToString() == Question);
        Assert.Contains(priyas.Messages.OfType<AGUIToolMessage>(), m => m.Content.ToString().Contains("WO-1005 was not found", StringComparison.Ordinal));
        Assert.Equal(2, Directory.EnumerateDirectories(agent.Conversations).Count());
    }

    [Fact]
    public async Task A_circuit_that_ends_mid_run_leaves_a_cancelled_run_in_the_agents_store()
    {
        // About a second of the run is left when the dispatcher goes: short enough that a client that drained the
        // response would let the agent finish it. The stream policy doesn't drain (MaxResponseDrainSize = 0).
        await using var agent = AgentHost.StartOnKestrel(settings: ("AI:ScriptedChunkDelayMilliseconds", "150"));
        await using var desk = new DeskHost(agent.Address);

        var (circuit, chat) = desk.OpenCircuit(DevelopmentUsers.Find("dana")!);
        await using (circuit)
        {
            // What disposing DeskChat does when a Desk replica stops: the run's request is cancelled.
            using var leaving = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var update in chat.GetStreamingResponseAsync(
                    [new ChatMessage(ChatRole.User, Question) { MessageId = "m-1" }], new ChatOptions { ConversationId = "t-leave" }, leaving.Token))
                {
                    if (update.Text.Length > 0)
                    {
                        await leaving.CancelAsync();
                    }
                }
            });
        }

        // The agent saw the request abort and recorded a stop, which Chapter 22 restates as cancelled.
        Conversation? conversation = null;
        for (var i = 0; i < 100 && conversation?.LastRunEnding != RunEnding.Cancelled; i++)
        {
            await Task.Delay(50, Ct);
            conversation = await agent.Store.FindAsync("dana", "t-leave", Ct);
        }

        Assert.Equal(RunEnding.Cancelled, conversation?.LastRunEnding);
    }

    private static async Task<string> AskAsync(IChatClient chat, string question, string threadId)
    {
        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, question) { MessageId = $"m-{Guid.NewGuid():N}" }],
            new ChatOptions { ConversationId = threadId },
            Ct);
        return response.Text;
    }

    [GeneratedRegex("""<input type="hidden" name="(?<name>[^"]+)" value="(?<value>[^"]*)""")]
    private static partial Regex HiddenInput();
}
