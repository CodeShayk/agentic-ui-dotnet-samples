using System.Net;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Bunit;
using Ch11.Beacon.ComponentViews;
using Ch11.Beacon.Desk.Components.Chat;
using Ch11.Beacon.Desk.Components.Generative;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch11.Tests;

/// <summary>
/// Beacon Desk rendering generated components from recorded streams: the real AGUIChatClient, tap and
/// conversation, with only the transport replaced, so these tests cover wire event to rendered DOM.
/// </summary>
public sealed class DeskRenderingTests : BunitContext
{
    private IAGUITransport? _transport;

    public DeskRenderingTests()
    {
        Services.AddLogging();
        Services.AddSingleton(_ => new WireEventTap(_transport ?? throw new InvalidOperationException("Call RenderChat first.")));
        Services.AddSingleton<IChatClient>(s => new AGUIChatClient(new AGUIChatClientOptions { Transport = s.GetRequiredService<WireEventTap>() }));
        Services.AddSingleton(s => new AgentConversation(s.GetRequiredService<IChatClient>(), new ConversationOptions { Tap = s.GetRequiredService<WireEventTap>() }));
        Services.AddSingleton(ComponentRegistry.CreateDefault());
        Services.AddSingleton(s => s.GetRequiredService<ComponentRegistry>().CreateProjector());

        JSInterop.Mode = JSRuntimeMode.Loose; // the composer calls FocusAsync
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public async Task A_card_renders_as_its_props_stream_and_offers_its_button_only_once_the_server_accepts_it()
    {
        var transport = new ReplayTransport(await Recordings.LoadAsync(Recordings.PartCard)) { Gated = true };
        var chat = RenderChat(transport);
        Ask(chat, "Which capacitor fits WO-1001?");

        // Partway through the arguments: a draft in the transcript's streaming slot, with what has arrived.
        transport.ReleaseThrough(e => e is ToolCallArgsEvent { Delta: var d } && d.Contains("5 uF", StringComparison.Ordinal));
        chat.WaitForAssertion(() =>
        {
            var card = chat.Find(".ch11-generated[data-phase=drafting]");
            Assert.Equal("Dual run capacitor 45/5 uF", card.QuerySelector(".ch11-part__name")!.TextContent.Trim());
            Assert.Equal("$38.50 each", card.QuerySelector(".ch11-part__price")!.TextContent.Trim());
            Assert.Null(card.QuerySelector(".ch11-part__stock"));               // not streamed yet
            Assert.Equal("Drafting", card.QuerySelector(".ch11-draft__badge")!.TextContent);
            Assert.Empty(card.QuerySelectorAll("button"));                      // nothing to act on in a preview
            Assert.Equal("true", card.Closest(".bd-message")!.GetAttribute("aria-busy"));
        });

        // TOOL_CALL_END: the arguments are complete, and the server is checking them.
        transport.ReleaseThrough(e => e is ToolCallEndEvent);
        chat.WaitForAssertion(() =>
        {
            Assert.Equal("Checking", chat.Find(".ch11-generated[data-phase=validating] .ch11-draft__badge").TextContent);
            Assert.Empty(chat.FindAll(".ch11-generated button"));
        });

        // TOOL_CALL_RESULT: accepted. The card settles, gets its button and is announced once.
        transport.ReleaseThrough(e => e is ToolCallResultEvent);
        chat.WaitForAssertion(() =>
        {
            var card = chat.Find(".ch11-generated[data-phase=ready]");
            Assert.Null(card.QuerySelector(".ch11-draft__badge"));
            Assert.Equal("24 in stock · Portsmouth", card.QuerySelector(".ch11-part__stock")!.TextContent.Trim());
            var order = card.QuerySelector("button")!;
            Assert.Equal("Order this part", order.TextContent);
            Assert.False(order.HasAttribute("disabled"));
            Assert.Equal("Beacon: Part card: CAP-45-5, Dual run capacitor 45/5 uF, $38.50, 24 in stock at Portsmouth, for WO-1001.",
                chat.Find("[data-bd-announcer]").TextContent.Trim());
        });

        transport.ReleaseAll();
        chat.WaitForAssertion(() => Assert.Equal("Ready", chat.Find(".bd-status__text").TextContent));
    }

    [Fact]
    public async Task An_invalid_card_falls_back_and_the_rest_of_the_answer_still_renders()
    {
        var chat = RenderChat(new ReplayTransport(await Recordings.LoadAsync(Recordings.InvalidCard)));
        Ask(chat, "Show me a card for part CAP-99");

        chat.WaitForAssertion(() =>
        {
            var fallback = chat.Find(".ch11-generated[data-phase=rejected] .ch11-fallback");
            Assert.Equal("Beacon couldn't show this part card.", fallback.QuerySelector(".ch11-fallback__title")!.TextContent);
            Assert.Equal("CAP-99 isn't a stocked part. Use a SKU from search_parts.", fallback.QuerySelector("li")!.TextContent);
            Assert.Empty(chat.FindAll(".ch11-part")); // the invented part is never drawn as a card
            Assert.StartsWith("I couldn't show that as a part card", chat.FindAll(".bd-markdown")[^1].TextContent.Trim());
            Assert.Equal("Ready", chat.Find(".bd-status__text").TextContent);
            Assert.False(chat.Find("input.bd-composer__input").HasAttribute("disabled"));
        });
    }

    [Fact]
    public async Task A_component_this_Desk_does_not_have_falls_back_with_the_servers_reason()
    {
        var chat = RenderChat(new ReplayTransport(await Recordings.LoadAsync(Recordings.UnknownComponent)));
        Ask(chat, "Show WO-1001 as an invoice");

        chat.WaitForAssertion(() =>
        {
            var fallback = chat.Find(".ch11-generated[data-tool=show_invoice_table] .ch11-fallback");
            Assert.Equal("Beacon tried to show a view this Desk doesn't have (show_invoice_table).", fallback.QuerySelector(".ch11-fallback__title")!.TextContent);
            Assert.StartsWith("WO-1001 is Scheduled", chat.FindAll(".bd-markdown")[^1].TextContent.Trim()); // the model recovered in text
        });
    }

    [Fact]
    public void A_request_the_agent_refuses_shows_the_status_it_answered_with()
    {
        var chat = RenderChat(new RefusingTransport(HttpStatusCode.Conflict));
        Ask(chat, "Which capacitor fits WO-1001?");

        chat.WaitForAssertion(() => Assert.Equal(
            "Beacon's agent refused the request (HTTP 409). Start a new chat if it happens again.",
            chat.Find(".bd-status__error").TextContent));
    }

    [Theory]
    [InlineData(FailureKind.ProtocolViolation, "Beacon's answer broke a protocol rule, so the Desk stopped reading it.")]
    [InlineData(FailureKind.StreamEnded, "The answer stopped unexpectedly. Try again.")]
    public void Each_kind_of_failure_has_its_own_words(FailureKind kind, string expected) =>
        Assert.Equal(expected, DeskChat.ErrorOf(ConversationView.Empty with { Status = ConversationStatus.Failed, Failure = new RunFailure(kind, "details") }));

    private IRenderedComponent<DeskChat> RenderChat(IAGUITransport transport)
    {
        _transport = transport;
        return Render<DeskChat>();
    }

    private void Ask(IRenderedComponent<DeskChat> chat, string text) =>
        // On the renderer's dispatcher, so no render can land between finding the elements and raising their events.
        Renderer.Dispatcher.InvokeAsync(() =>
        {
            chat.Find("input.bd-composer__input").Input(text);
            chat.Find("form.bd-composer").Submit();
        }).GetAwaiter().GetResult();
}

/// <summary>The error boundary around every generated component.</summary>
public sealed class ErrorBoundaryTests : BunitContext
{
    public ErrorBoundaryTests()
    {
        Services.AddLogging();
        Services.AddSingleton(new ComponentRegistry().Register<ThrowingCard, PartCardView>(ComponentKinds.PartCard));
    }

    [Fact]
    public void A_component_that_throws_while_rendering_costs_one_card_not_the_page()
    {
        var ready = new GeneratedComponent("call-1", "show_part_card")
        {
            ComponentName = "PartCard",
            DisplayName = "part card",
            Phase = ComponentPhase.Ready,
            View = new PartCardView { Sku = "CAP-45-5" },
        };

        var host = Render<GeneratedComponentHost>(p => p.Add(h => h.Component, ready));

        Assert.Equal("This view failed to draw. The rest of the conversation is unaffected.", host.Find(".ch11-fallback__reason").TextContent);
        Assert.Equal("Beacon couldn't show this part card.", host.Find(".ch11-fallback__title").TextContent);
    }

    /// <summary>A component with a bug: it throws while rendering.</summary>
    public sealed class ThrowingCard : GeneratedComponentBase<PartCardView>
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            throw new InvalidOperationException("A bug in a generated component.");
    }
}

/// <summary>An agent that refuses every run with an HTTP status before streaming anything, as a gate or a validator would.</summary>
public sealed class RefusingTransport(HttpStatusCode status) : IAGUITransport
{
    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (status is not HttpStatusCode.OK)
        {
            throw new HttpRequestException($"Response status code does not indicate success: {(int)status}.", null, status);
        }

        yield break;
    }
}
