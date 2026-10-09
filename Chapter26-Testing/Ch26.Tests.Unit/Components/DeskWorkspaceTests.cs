using System.Net;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Bunit;
using Ch26.Beacon.Desk.Workspace;
using Ch26.Tests.Unit.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch26.Tests.Unit.Components;

/// <summary>
/// The whole Desk workspace against streams captured from Ch26.Beacon.Agent. Only the transport is
/// replaced; WireEventTap, AGUIChatClient, AgentConversation and every component are the real ones.
/// Because the streams are recordings, these tests can't notice a change on the server: that's the
/// integration layer's job.
/// </summary>
public sealed class DeskWorkspaceTests : BunitContext
{
    private IAGUITransport? _transport;

    public DeskWorkspaceTests()
    {
        Services.AddLogging();
        Services.AddSingleton(_ => new WireEventTap(_transport ?? throw new InvalidOperationException("Call RenderWorkspace first.")));
        Services.AddSingleton<IChatClient>(services => new AGUIChatClient(new AGUIChatClientOptions { Transport = services.GetRequiredService<WireEventTap>() }));
        Services.AddSingleton(services => new AgentConversation(
            services.GetRequiredService<IChatClient>(),
            new ConversationOptions { Tap = services.GetRequiredService<WireEventTap>() }));

        JSInterop.Mode = JSRuntimeMode.Loose; // focus calls
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public async Task Asking_about_a_work_order_opens_it_in_the_work_area()
    {
        var desk = RenderWorkspace(new ReplayTransport(await ReplayTransport.LoadAsync("wo-1001-status.sse")));

        Ask(desk, "What is the status of WO-1001?");

        desk.WaitForAssertion(() =>
        {
            Assert.Equal("Ready", desk.Find(".bd-status__text").TextContent);
            Assert.Equal("WO-1001", desk.Find(".ch26-workorder h1").TextContent);
            Assert.StartsWith("WO-1001 is Scheduled", desk.FindAll(".bd-markdown")[^1].TextContent.Trim());
        });
    }

    [Fact]
    public async Task An_approval_blocks_the_composer_until_Dana_answers_and_her_answer_resumes_the_run()
    {
        var transport = new ReplayTransport(
            await ReplayTransport.LoadAsync("order-compressor.sse"),
            await ReplayTransport.LoadAsync("order-compressor-approved.sse"));
        var desk = RenderWorkspace(transport);

        Ask(desk, "Order a replacement compressor CMP-CA36 for WO-1001.");
        desk.WaitForAssertion(() =>
        {
            Assert.Equal("Approve this part order?", desk.Find("[role=dialog] h3").TextContent);
            Assert.Equal(["WO-1001", "CMP-CA36", "1"], desk.FindAll("[role=dialog] dd").Select(dd => dd.TextContent));
            Assert.True(desk.Find("input.bd-composer__input").HasAttribute("disabled"));
        });

        desk.Find("[role=dialog] button.bd-button--primary").Click();

        desk.WaitForAssertion(() =>
        {
            Assert.Empty(desk.FindAll("[role=dialog]"));
            Assert.StartsWith("Order PO-9001 is placed", desk.FindAll(".bd-markdown")[^1].TextContent.Trim());
        });

        // The second run is the resume: the server's interrupt ID, answered with approval.
        var resume = Assert.Single(transport.Inputs[1].Resume!);
        Assert.StartsWith("apr_", resume.InterruptId);
        Assert.True(resume.Payload!.Value.GetProperty("approved").GetBoolean());
    }

    [Fact]
    public void A_request_the_agent_refuses_reads_as_a_refusal_not_as_an_outage()
    {
        // A 4xx: AGUIChatClient's HTTP transport throws this, and Beacon.Client.Core reports it as Rejected.
        var desk = RenderWorkspace(new RefusingTransport(HttpStatusCode.BadRequest));

        Ask(desk, "What is the status of WO-1001?");

        desk.WaitForAssertion(() => Assert.Equal(
            "Beacon's agent refused the request (HTTP 400). Start a new chat if it happens again.",
            desk.Find(".bd-status__error[role=alert]").TextContent));
    }

    private IRenderedComponent<DeskWorkspace> RenderWorkspace(IAGUITransport transport)
    {
        _transport = transport;
        return Render<DeskWorkspace>(p => p.Add(c => c.RenderInterval, TimeSpan.Zero));
    }

    private void Ask(IRenderedComponent<DeskWorkspace> desk, string text) =>
        // On the renderer's dispatcher, so no render lands between finding the elements and raising events.
        Renderer.Dispatcher.InvokeAsync(() =>
        {
            desk.Find("input.bd-composer__input").Input(text);
            desk.Find("form.bd-composer").Submit();
        }).GetAwaiter().GetResult();
}

/// <summary>A transport whose agent answers every run with an HTTP error status instead of a stream.</summary>
internal sealed class RefusingTransport(HttpStatusCode status) : IAGUITransport
{
    public IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, CancellationToken cancellationToken) =>
        throw new HttpRequestException($"Response status code does not indicate success: {(int)status}.", null, status);
}
