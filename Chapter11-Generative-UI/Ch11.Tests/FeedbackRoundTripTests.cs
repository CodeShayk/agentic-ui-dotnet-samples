using System.Text.Json;
using AGUI.Client;
using Beacon.Client.Core;
using Beacon.Domain;
using Bunit;
using Ch11.Beacon.Components;
using Ch11.Beacon.ComponentViews;
using Ch11.Beacon.Desk.Components.Chat;
using Ch11.Beacon.Desk.Components.Generative;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch11.Tests;

/// <summary>
/// A button in a generated card, all the way round: Beacon Desk (bUnit) over HTTP to the in-process agent
/// (scripted model), the click becoming the next user turn, and the agent's order tool doing the work.
/// </summary>
public sealed class FeedbackRoundTripTests : BunitContext
{
    // Long enough for a cold host on a slow machine; a passing run never waits this long.
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly ServerAuthorityTests.AgentFactory _agent = new();
    private readonly List<string> _requests = [];

    public FeedbackRoundTripTests()
    {
        // The Desk's client stack, pointed at the in-process agent; a handler keeps every request body.
        Services.AddLogging();
        Services.AddSingleton(_ => WireEventTap.ForHttp(_agent.CreateDefaultClient(new RecordingHandler(_requests)), "http://localhost/agent"));
        Services.AddSingleton<IChatClient>(s => new AGUIChatClient(new AGUIChatClientOptions { Transport = s.GetRequiredService<WireEventTap>() }));
        Services.AddSingleton(s => new AgentConversation(s.GetRequiredService<IChatClient>(), new ConversationOptions { Tap = s.GetRequiredService<WireEventTap>() }));
        Services.AddSingleton(ComponentRegistry.CreateDefault());
        Services.AddSingleton(s => s.GetRequiredService<ComponentRegistry>().CreateProjector());

        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public async Task Order_this_part_becomes_the_next_user_turn_and_the_agent_places_the_order()
    {
        var chat = Render<DeskChat>();
        var conversation = Services.GetRequiredService<AgentConversation>();
        Ask(chat, "Which capacitor fits WO-1001?");

        // The button is enabled as soon as the card's result arrives, while the run is still answering, and
        // the Desk ignores a click until the run has finished. Click when a person's click would count.
        chat.WaitForAssertion(() =>
        {
            Assert.False(chat.Find(".ch11-generated[data-phase=ready] button").HasAttribute("disabled"));
            Assert.Equal(ConversationStatus.Idle, conversation.View.Status);
        }, Wait);

        await Renderer.Dispatcher.InvokeAsync(() => chat.Find(".ch11-generated[data-phase=ready] button").Click());

        chat.WaitForAssertion(() =>
        {
            Assert.Equal("Order PO-9001 is placed: 1 x CAP-45-5 for $38.50.", chat.FindAll(".bd-markdown")[^1].TextContent.Trim());
            var button = chat.Find(".ch11-generated[data-phase=ready] button");
            Assert.True(button.HasAttribute("disabled")); // one click, one order
            Assert.Equal("Requested", chat.Find(".ch11-actions__reason").TextContent);
        }, Wait);

        // The click went to the agent as a user message, after the card's call and its accepted result.
        using var second = JsonDocument.Parse(_requests[1]);
        var messages = second.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(("user", "Order one CAP-45-5 for WO-1001."), (messages[^1].GetProperty("role").GetString(), messages[^1].GetProperty("content").GetString()));
        Assert.Contains(messages, m => m.TryGetProperty("toolCalls", out var calls) && calls.EnumerateArray().Any(c => c.GetProperty("function").GetProperty("name").GetString() == "show_part_card"));

        // And the order exists, placed by the agent's own tool with its own rules.
        var order = Assert.Single(_agent.Services.GetRequiredService<IFieldServiceStore>().ListPartOrders("WO-1001"));
        Assert.Equal(("CAP-45-5", 1), (order.Sku, order.Quantity));
    }

    [Fact]
    public void Only_a_ready_component_with_a_granted_action_produces_a_message()
    {
        var props = JsonDocument.Parse("""{"sku":"CAP-45-5","workOrderId":"WO-1001"}""").RootElement.Clone();
        var ready = new GeneratedComponent("call-1", "show_part_card")
        {
            ComponentName = "PartCard",
            Phase = ComponentPhase.Ready,
            View = new PartCardView { Sku = "CAP-45-5", WorkOrderId = "WO-1001" },
            Props = props,
            Actions = [new ComponentAction("order", "Order this part", Enabled: true)],
        };

        Assert.Equal("Order one CAP-45-5 for WO-1001.", ComponentActionMessages.Compose(new(ready, "order")));
        Assert.Null(ComponentActionMessages.Compose(new(ready with { Phase = ComponentPhase.Drafting }, "order")));   // a preview
        Assert.Null(ComponentActionMessages.Compose(new(ready with { Actions = [] }, "order")));                       // not granted
        Assert.Null(ComponentActionMessages.Compose(new(ready.WithActionTaken("order"), "order")));                    // already pressed
        Assert.Null(ComponentActionMessages.Compose(new(ready, "refund")));                                           // not an action the card has
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _agent.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Ask(IRenderedComponent<DeskChat> chat, string text) =>
        Renderer.Dispatcher.InvokeAsync(() =>
        {
            chat.Find("input.bd-composer__input").Input(text);
            chat.Find("form.bd-composer").Submit();
        }).GetAwaiter().GetResult();

    private sealed class RecordingHandler(List<string> requests) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (requests)
            {
                requests.Add(request.Content is null ? string.Empty : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
