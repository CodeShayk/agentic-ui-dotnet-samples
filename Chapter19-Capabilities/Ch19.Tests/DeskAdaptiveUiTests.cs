using Beacon.Client.Core;
using Bunit;
using Ch19.Beacon.Adaptive.Planning;
using Ch19.Beacon.Agent.Capabilities;
using Ch19.Beacon.Desk.Components;
using Ch19.Beacon.Desk.Session;
using Ch19.Tests.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Ch19.Tests;

/// <summary>
/// Beacon Desk's workspace rendered with bUnit against the real agent in memory: the same component,
/// three agents (full, reduced, and one that publishes nothing), three coherent screens.
/// </summary>
public sealed class DeskAdaptiveUiTests : BunitContext
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task The_full_agent_gets_photo_and_PDF_inputs_and_a_reasoning_panel()
    {
        await using var host = new AgentHost(CapabilityProfile.Full);
        var (desk, session) = Render(host.CreateClient(), host.Endpoint);

        Assert.Equal(["PhotoInput", "DocumentInput"], desk.FindAll("[data-input]").Select(e => e.GetAttribute("data-input")));
        Assert.Empty(desk.FindAll("[data-note]"));
        Assert.Equal("full", desk.Find("[data-testid=profile]").TextContent);
        Assert.Contains("Ask Beacon to diagnose", desk.Find("[data-testid=reasoning-panel]").TextContent, StringComparison.Ordinal);

        await session.SendAsync("Why would EQ-501 short-cycle? Diagnose it.");

        desk.WaitForAssertion(() =>
        {
            Assert.Contains("A weak run capacitor", desk.Find("[data-testid=reasoning-panel]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Speaks AG-UI 1.0", desk.Find("[data-testid=protocol]").TextContent, StringComparison.Ordinal);
        }, Wait);
    }

    [Fact]
    public async Task The_reduced_agent_gets_a_smaller_screen_that_says_why()
    {
        await using var host = new AgentHost(CapabilityProfile.Reduced);
        var (desk, session) = Render(host.CreateClient(), host.Endpoint);

        Assert.Empty(desk.FindAll("[data-input]"));
        Assert.Contains("can't read photos", desk.Find("[data-note=PhotoInput]").TextContent, StringComparison.Ordinal);
        Assert.Contains("can't read PDFs", desk.Find("[data-note=DocumentInput]").TextContent, StringComparison.Ordinal);
        Assert.Contains("can't open work orders", desk.Find("[data-note=ScreenTools]").TextContent, StringComparison.Ordinal);
        Assert.Equal("hidden", desk.Find("[data-feature=ReasoningPanel] td:last-child").TextContent);
        Assert.Empty(desk.FindAll("[data-testid=reasoning-panel]"));

        await session.SendAsync("Why would EQ-501 short-cycle? Diagnose it.");

        // Renders are throttled, so the protocol card can show before the answer: wait for both, then
        // check that the finished run left no reasoning panel.
        desk.WaitForAssertion(() =>
        {
            Assert.Contains("Speaks AG-UI 1.0", desk.Find("[data-testid=protocol]").TextContent, StringComparison.Ordinal);
            Assert.Contains("failing run capacitor", desk.Find(".bd-chat-panel").TextContent, StringComparison.Ordinal);
        }, Wait);
        Assert.Empty(desk.FindAll("[data-testid=reasoning-panel]"));
    }

    [Theory]
    [InlineData(CapabilityProfile.Full, true)]
    [InlineData(CapabilityProfile.Reduced, false)]
    public async Task The_screen_tool_opens_the_work_order_only_where_the_agent_accepts_it(CapabilityProfile profile, bool opened)
    {
        await using var host = new AgentHost(profile);
        var (desk, session) = Render(host.CreateClient(), host.Endpoint);

        await session.SendAsync("Open WO-1001");

        desk.WaitForAssertion(() => Assert.Equal(ConversationStatus.Idle, session.Conversation!.View.Status), Wait);
        desk.WaitForAssertion(() => Assert.Equal(opened, desk.FindAll("[data-testid=open-work-order]").Count == 1), Wait);
        if (!opened)
        {
            // Answered with the agent's own get_work_order instead.
            desk.WaitForAssertion(() => Assert.Contains("Harbor View Dental", desk.Find(".bd-chat-panel").TextContent, StringComparison.Ordinal), Wait);
        }
    }

    [Fact]
    public async Task An_agent_that_publishes_nothing_gets_unconfirmed_inputs_and_reasoning_when_it_streams()
    {
        await using var stock = await StockAgentHost.StartAsync();
        var (desk, session) = Render(stock.CreateClient(), stock.Endpoint);

        Assert.Contains("doesn't publish its capabilities", desk.Find("[data-testid=undeclared]").TextContent, StringComparison.Ordinal);
        Assert.Equal(["PhotoInput", "DocumentInput"], desk.FindAll("[data-input]").Select(e => e.GetAttribute("data-input")));
        Assert.Contains("hasn't said whether it reads photos", desk.Find("[data-note=PhotoInput]").TextContent, StringComparison.Ordinal);
        Assert.Empty(desk.FindAll("[data-testid=reasoning-panel]"));

        await session.SendAsync("Why would EQ-501 short-cycle? Diagnose it.");

        desk.WaitForAssertion(() =>
        {
            Assert.Contains("A weak run capacitor", desk.Find("[data-testid=reasoning-panel]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Didn't declare a protocol version", desk.Find("[data-testid=protocol]").TextContent, StringComparison.Ordinal);
        }, Wait);
    }

    [Fact]
    public async Task A_redeployed_agent_that_refuses_an_attachment_gets_a_refusal_not_an_outage()
    {
        // The Desk read the full deployment's document; by the time Dana sends, the reduced one is serving.
        await using var before = new AgentHost(CapabilityProfile.Full);
        await using var after = new AgentHost(CapabilityProfile.Reduced);
        using var http = new HttpClient(new Redeployed(before.Server.CreateHandler(), after.Server.CreateHandler())) { BaseAddress = before.Server.BaseAddress };
        var (desk, session) = Render(http, before.Endpoint);
        // On the renderer's dispatcher, as the composer does it: the tray is what the composer renders.
        await desk.InvokeAsync(() => Assert.Null(session.Attachments.TryAdd(new MediaAttachment("nameplate.png", "image/png", TestMedia.Png()))));
        await desk.InvokeAsync(() => session.SendAsync("Read this nameplate."));

        // A 400 before the stream: Rejected, not Unreachable, and the Desk says what to do about it.
        Assert.Equal((FailureKind.Rejected, System.Net.HttpStatusCode.BadRequest), (session.Conversation!.View.Failure!.Kind, session.Conversation.View.Failure.StatusCode));
        desk.WaitForAssertion(() => Assert.Contains(
            "refused the request (HTTP 400), so nothing ran. If it was redeployed, start a new chat",
            desk.Find(".bd-chat-panel").TextContent, StringComparison.Ordinal), Wait);
    }

    private (IRenderedComponent<DeskWorkspace> Desk, DeskSession Session) Render(HttpClient http, Uri endpoint)
    {
        var session = new DeskSession(http, endpoint);
        Services.AddSingleton(session); // services first: touching JSInterop or the renderer freezes the container
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        var desk = Render<DeskWorkspace>();
        // Rendered after discovery: the card names the agent, or says it described nothing.
        desk.WaitForState(() => desk.FindAll("[data-testid=identity], [data-testid=undeclared]").Count == 1, Wait);
        return (desk, session);
    }

    // GET (discovery) goes to the deployment the Desk saw first; POST (runs) to the one serving now.
    private sealed class Redeployed(HttpMessageHandler before, HttpMessageHandler after) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _before = new(before);
        private readonly HttpMessageInvoker _after = new(after);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            (request.Method == HttpMethod.Get ? _before : _after).SendAsync(request, cancellationToken);
    }
}
