using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Bunit;
using Ch09.Beacon.Desk.Components.Chat;
using Ch09.Tests.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch09.Tests;

/// <summary>
/// Beacon Desk's chips and cards against streams captured from Ch09.Beacon.Agent. Only the transport is
/// replaced: the WireEventTap, AGUIChatClient, AgentConversation and DeskChat are the real ones, so these
/// tests cover the path from wire event to rendered card, one released event at a time.
/// </summary>
public sealed class DeskToolCardsTests : BunitContext
{
    private ReplayTransport? _transport;

    public DeskToolCardsTests()
    {
        Services.AddLogging();
        Services.AddSingleton(_ => new WireEventTap(_transport ?? throw new InvalidOperationException("Call RenderChat first.")));
        Services.AddSingleton<IChatClient>(services => new AGUIChatClient(new AGUIChatClientOptions { Transport = services.GetRequiredService<WireEventTap>() }));
        Services.AddSingleton(services => new AgentConversation(
            services.GetRequiredService<IChatClient>(),
            new ConversationOptions { Tap = services.GetRequiredService<WireEventTap>() }));

        JSInterop.Mode = JSRuntimeMode.Loose; // the composer calls FocusAsync
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public async Task Three_parallel_calls_render_independently_and_in_call_order()
    {
        var transport = new ReplayTransport(await Recordings.LoadAsync(Recordings.PrepareWo1001)) { Gated = true };
        var chat = RenderChat(transport);
        Ask(chat, "Prepare WO-1001 for Priya");

        // Two calls have started and the second one's arguments are half written. Only the tap shows
        // this: AGUIChatClient yields nothing for TOOL_CALL_START or TOOL_CALL_ARGS.
        transport.ReleaseThrough(e => e is ToolCallArgsEvent { Delta: "\"model" });
        chat.WaitForAssertion(() => Assert.Equal(
            [("preparing", "Looking up WO-1001…"), ("preparing", "Searching parts…")],
            Chips(chat)));

        // The third call starts; nothing finishes or reorders the first two.
        transport.ReleaseThrough(e => e is ToolCallArgsEvent { ToolCallId: "call-0003-3", Delta: "d\":\"EQ" });
        chat.WaitForAssertion(() => Assert.Equal(
            [("preparing", "Looking up WO-1001…"), ("preparing", "Searching parts that fit the CA-36X…"), ("preparing", "Checking the warranty on EQ…")],
            Chips(chat)));

        // TOOL_CALL_END for all three: the arguments are complete and the tools are running.
        transport.ReleaseThrough(e => e is ToolCallEndEvent { ToolCallId: "call-0003-3" });
        chat.WaitForAssertion(() =>
        {
            Assert.Equal(
                [("running", "Looking up WO-1001…"), ("running", "Searching parts that fit the CA-36X…"), ("running", "Checking the warranty on EQ-501…")],
                Chips(chat));
            Assert.Empty(chat.FindAll(".ch09-card, .ch09-warranty"));
            Assert.All(chat.FindAll(".ch09-tool"), li => Assert.Equal("true", li.GetAttribute("aria-busy")));
        });

        // The results: a card for each, in call order.
        transport.ReleaseThrough(e => e is ToolCallResultEvent { ToolCallId: "call-0003-3" });
        chat.WaitForAssertion(() =>
        {
            Assert.Equal(
                [("succeeded", "Looked up WO-1001"), ("succeeded", "Searched parts that fit the CA-36X"), ("succeeded", "Checked the warranty on EQ-501")],
                Chips(chat));
            Assert.Equal(["ch09-card--work-order", "ch09-card--parts", "ch09-warranty--covered"], CardKinds(chat));
            Assert.Equal("Harbor View Dental", chat.Find(".ch09-card--work-order dd").TextContent);
            Assert.Equal(5, chat.FindAll(".ch09-parts tbody tr").Count);
            Assert.Equal("Covered", chat.Find(".ch09-warranty__status").TextContent);
        });

        // Then the answer, after the cards.
        transport.ReleaseAll();
        chat.WaitForAssertion(() =>
        {
            Assert.Equal("Ready", chat.Find(".bd-status__text").TextContent);
            var items = chat.FindAll(".bd-transcript__list > li");
            Assert.Equal(3, items.Count); // question, lookups, answer
            Assert.NotNull(items[1].QuerySelector(".ch09-tools"));
            Assert.StartsWith("WO-1001 is Scheduled for 09:00 with Priya Raman", items[2].QuerySelector(".bd-markdown")!.TextContent.Trim());
        });
    }

    [Fact]
    public async Task A_failed_lookup_fails_alone()
    {
        var transport = new ReplayTransport(await Recordings.LoadAsync(Recordings.PrepareWo1005));
        var chat = RenderChat(transport);
        Ask(chat, "Prepare WO-1005 for Elena");

        chat.WaitForAssertion(() =>
        {
            Assert.Equal("Ready", chat.Find(".bd-status__text").TextContent);
            Assert.Equal(
                [("succeeded", "Looked up WO-1005"), ("succeeded", "Searched parts that fit the RT-120"), ("failed", "Checking the warranty on EQ-701 failed")],
                Chips(chat));
            Assert.Equal(["ch09-card--work-order", "ch09-card--parts"], CardKinds(chat));
            Assert.StartsWith("The warranty service did not answer", chat.Find(".ch09-tool--failed .ch09-tool__error").TextContent);
            Assert.DoesNotContain("internal", chat.Markup);
        });
    }

    [Fact]
    public async Task Results_that_arrive_out_of_order_still_render_in_call_order()
    {
        // The protocol lets a producer answer concurrent calls in any order. This is the captured
        // stream with its three TOOL_CALL_RESULT events reversed.
        var recorded = await Recordings.LoadAsync(Recordings.PrepareWo1001);
        var results = recorded.OfType<ToolCallResultEvent>().Reverse().ToList();
        var first = recorded.ToList().FindIndex(e => e is ToolCallResultEvent);
        List<BaseEvent> reordered = [.. recorded.Take(first), .. results, .. recorded.Skip(first + results.Count)];

        var chat = RenderChat(new ReplayTransport(reordered));
        Ask(chat, "Prepare WO-1001 for Priya");

        chat.WaitForAssertion(() =>
        {
            Assert.Equal("Ready", chat.Find(".bd-status__text").TextContent);
            Assert.Equal(["get_work_order", "search_parts", "check_warranty"], chat.FindAll(".ch09-chip__name").Select(n => n.TextContent));
            Assert.Equal(["ch09-card--work-order", "ch09-card--parts", "ch09-warranty--covered"], CardKinds(chat));
        });
    }

    [Fact]
    public async Task Stop_while_a_tool_runs_marks_the_call_stopped()
    {
        var recorded = await Recordings.LoadAsync(Recordings.WorkOrderStatus);
        var throughEnd = recorded.TakeWhile(e => e is not ToolCallResultEvent).ToList();
        var transport = new ReplayTransport(throughEnd) { HoldOpen = true };
        var chat = RenderChat(transport);
        Ask(chat, "What's the status of WO-1001?");

        chat.WaitForAssertion(() => Assert.Equal([("running", "Looking up WO-1001…")], Chips(chat)));

        chat.Find("button.bd-button--stop").Click();
        await transport.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        chat.WaitForAssertion(() =>
        {
            Assert.Equal([("stopped", "Looking up WO-1001: stopped before it finished")], Chips(chat));
            Assert.Equal("false", chat.Find(".ch09-tool").GetAttribute("aria-busy"));
        });
    }

    [Fact]
    public async Task The_checklist_write_shows_what_changed()
    {
        var chat = RenderChat(new ReplayTransport(await Recordings.LoadAsync(Recordings.Checklist)));
        Ask(chat, "Mark C3 done on WO-1001");

        chat.WaitForAssertion(() =>
        {
            Assert.Equal([("succeeded", "Updated C3 on WO-1001")], Chips(chat));
            Assert.Equal("C3 (Inspect run capacitor) is done. 1 of 4 items done.", Normalize(chat.Find(".ch09-checklist").TextContent));
        });
    }

    [Fact]
    public void A_refused_request_says_so_in_the_Desks_words()
    {
        var chat = RenderChat(new ReplayTransport([])
        {
            Throws = new HttpRequestException("Response status code does not indicate success: 409 (Conflict).", null, System.Net.HttpStatusCode.Conflict),
        });
        Ask(chat, "Prepare WO-1001 for Priya");

        chat.WaitForAssertion(() => Assert.Equal(
            "Beacon's agent refused the request (HTTP 409). Start a new chat if it happens again.",
            chat.Find(".bd-status__error").TextContent));
    }

    [Fact]
    public void A_stream_that_breaks_a_protocol_rule_is_named_as_one()
    {
        // A STATE_DELTA must be a JSON array of operations; this one is a lone operation.
        using var patch = System.Text.Json.JsonDocument.Parse("""{"op":"add","path":"/status","value":"done"}""");
        var chat = RenderChat(new ReplayTransport(
        [
            new RunStartedEvent { ThreadId = "t", RunId = "r" },
            new StateDeltaEvent { Delta = patch.RootElement.Clone() },
            new RunFinishedEvent { ThreadId = "t", RunId = "r" },
        ]));
        Ask(chat, "Prepare WO-1001 for Priya");

        chat.WaitForAssertion(() => Assert.Equal(
            "Beacon's answer broke a protocol rule, so the Desk stopped reading it.",
            chat.Find(".bd-status__error").TextContent));
    }

    private IRenderedComponent<DeskChat> RenderChat(ReplayTransport transport)
    {
        _transport = transport;
        return Render<DeskChat>();
    }

    private void Ask(IRenderedComponent<DeskChat> chat, string text) =>
        // On the renderer's dispatcher, so no render can land between finding the elements and raising
        // their events, which would leave a stale event handler ID.
        Renderer.Dispatcher.InvokeAsync(() =>
        {
            chat.Find("input.bd-composer__input").Input(text);
            chat.Find("form.bd-composer").Submit();
        }).GetAwaiter().GetResult();

    private static List<(string State, string Headline)> Chips(IRenderedComponent<DeskChat> chat) =>
        [.. chat.FindAll(".ch09-tool").Select(li => (
            li.ClassList.First(c => c.StartsWith("ch09-tool--", StringComparison.Ordinal))["ch09-tool--".Length..],
            li.QuerySelector(".ch09-chip__text")!.TextContent))];

    private static List<string> CardKinds(IRenderedComponent<DeskChat> chat) =>
        [.. chat.FindAll(".ch09-card, .ch09-warranty").Select(e => e.ClassList.First(c =>
            c.StartsWith("ch09-card--", StringComparison.Ordinal) || c.StartsWith("ch09-warranty--", StringComparison.Ordinal)))];

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
