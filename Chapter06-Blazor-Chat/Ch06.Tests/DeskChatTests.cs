using AGUI.Abstractions;
using AGUI.Client;
using Bunit;
using Ch06.Beacon.Desk.Client.Chat;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch06.Tests;

/// <summary>
/// Beacon Desk's chat against recorded AG-UI streams. The real AGUIChatClient converts the events;
/// only the transport is replaced, so these tests cover the path from wire event to rendered DOM.
/// </summary>
public sealed class DeskChatTests : BunitContext
{
    private ReplayTransport? _transport;

    public DeskChatTests()
    {
        // The real AGUIChatClient, over whichever recorded stream the test chose.
        Services.AddLogging();
        Services.AddSingleton<IChatClient>(_ => new AGUIChatClient(new AGUIChatClientOptions
        {
            Transport = _transport ?? throw new InvalidOperationException("Call RenderChat first."),
        }));

        JSInterop.Mode = JSRuntimeMode.Loose; // the composer calls FocusAsync
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public async Task A_recorded_run_renders_one_update_at_a_time()
    {
        var transport = new ReplayTransport(await Recordings.LoadAsync(Recordings.WorkOrderStatus)) { Gated = true };
        var chat = RenderChat(transport);

        Ask(chat, "What's the status of WO-1001?");

        // The question appears at once, and Beacon shows it's working before any event arrives.
        chat.WaitForAssertion(() =>
        {
            Assert.Equal("What's the status of WO-1001?", chat.Find(".bd-message--user .bd-message__text").TextContent);
            Assert.Equal("Beacon is responding…", chat.Find(".bd-status__text").TextContent);
            Assert.Single(chat.FindAll(".bd-message--pending"));
        });

        // RUN_STARTED and the backend tool call: the run ID shows, but there's no text yet.
        transport.ReleaseThrough(e => e is ToolCallResultEvent);
        chat.WaitForAssertion(() =>
        {
            Assert.Equal(transport.Inputs[0].RunId, chat.FindAll(".bd-status__id code")[1].TextContent);
            Assert.Empty(chat.FindAll(".bd-message--streaming"));
        });

        // Each TEXT_MESSAGE_CONTENT extends the streaming message, rendered as Markdown.
        transport.ReleaseThrough(e => e is TextMessageContentEvent);
        chat.WaitForAssertion(() =>
        {
            Assert.Equal("WO-1001 is Scheduled", StreamingText(chat));
            Assert.Equal("WO-1001", chat.Find(".bd-message--streaming strong").TextContent);
        });

        transport.ReleaseThrough(e => e is TextMessageContentEvent);
        chat.WaitForAssertion(() => Assert.Equal("WO-1001 is Scheduled (High priority): Air", StreamingText(chat)));

        // The rest of the run: the message moves into the transcript and is announced once.
        transport.ReleaseAll();
        chat.WaitForAssertion(() =>
        {
            Assert.Empty(chat.FindAll(".bd-message--streaming"));
            var answer = Assert.Single(chat.FindAll(".bd-message--assistant"));
            Assert.Equal(
                "WO-1001 is Scheduled (High priority): Air conditioner short-cycling and blowing warm air. " +
                "Customer: Harbor View Dental; equipment: Contoso Air CA-36X.",
                answer.QuerySelector(".bd-markdown")!.TextContent.Trim());
            Assert.Equal("Ready", chat.Find(".bd-status__text").TextContent);
            Assert.StartsWith("Beacon: WO-1001 is Scheduled", chat.Find("[aria-live=polite]").TextContent.Trim());
        });
    }

    [Fact]
    public async Task The_next_run_resends_the_whole_history_on_the_same_thread()
    {
        var transport = new ReplayTransport(await Recordings.LoadAsync(Recordings.WorkOrderStatus));
        var chat = RenderChat(transport);

        Ask(chat, "What's the status of WO-1001?");
        WaitForAnswers(chat, 1);
        Ask(chat, "Is EQ-501 under warranty?");
        WaitForAnswers(chat, 2);

        var (first, second) = (transport.Inputs[0], transport.Inputs[1]);
        Assert.Equal(first.ThreadId, second.ThreadId);
        Assert.NotEqual(first.RunId, second.RunId);

        // The question, the tool call, its result, the answer, then the new question.
        Assert.Equal([AGUIRoles.User, AGUIRoles.Assistant, AGUIRoles.Tool, AGUIRoles.Assistant, AGUIRoles.User],
            second.Messages.Select(m => m.Role));
        Assert.All(second.Messages, m => Assert.False(string.IsNullOrEmpty(m.Id)));
        Assert.Equal(first.Messages[0].Id, second.Messages[0].Id);
    }

    [Fact]
    public async Task Stop_cancels_the_run_and_keeps_what_was_shown()
    {
        // The run's start and its first three text fragments, then silence until someone stops it.
        var handover = await Recordings.LoadAsync(Recordings.HandoverNote);
        var transport = new ReplayTransport([.. handover.Take(5)]) { HoldOpen = true };
        var chat = RenderChat(transport);

        Ask(chat, "Write a handover note for WO-1001");
        chat.WaitForAssertion(() => Assert.StartsWith("Here's a handover", StreamingText(chat)));

        chat.Find("button.bd-button--stop").Click();

        // The transport saw the cancellation: AGUIChatClient aborted the request.
        await transport.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        chat.WaitForAssertion(() =>
        {
            Assert.Equal("Stopped. The partial answer is kept.", chat.Find(".bd-status__text").TextContent);
            var partial = chat.Find(".bd-message--partial");
            Assert.StartsWith("Here's a handover", partial.QuerySelector(".bd-markdown")!.TextContent.Trim());
            Assert.Contains("Stopped before Beacon finished.", partial.TextContent);
            Assert.True(chat.Find("button.bd-button--stop").HasAttribute("disabled"));
            Assert.False(chat.Find("input.bd-composer__input").HasAttribute("disabled"));
        });

        // Focus went to the composer when the message was sent and again after the stop.
        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 2);

        // The next run carries the partial answer as plain assistant text.
        Ask(chat, "Go on");
        chat.WaitForAssertion(() => Assert.Equal(2, transport.Inputs.Count));
        var resent = transport.Inputs[1].Messages;
        Assert.Equal([AGUIRoles.User, AGUIRoles.Assistant, AGUIRoles.User], resent.Select(m => m.Role));
        Assert.StartsWith("Here's a handover", Assert.IsType<AGUIAssistantMessage>(resent[1]).Content);
        chat.Find("button.bd-button--stop").Click();
    }

    [Fact]
    public void A_run_error_is_announced_and_the_chat_stays_usable()
    {
        var transport = new ReplayTransport(
        [
            new RunStartedEvent { ThreadId = "t", RunId = "r" },
            new RunErrorEvent { Message = "The agent couldn't finish this run.", Code = "StreamingError" },
        ]);
        var chat = RenderChat(transport);

        Ask(chat, "What's the status of WO-1001?");

        chat.WaitForAssertion(() =>
        {
            Assert.Equal("Something went wrong.", chat.Find(".bd-status__text").TextContent);
            Assert.Equal("The agent couldn't finish this run.", chat.Find("[role=alert]").TextContent);
            Assert.False(chat.Find("button[type=submit]").HasAttribute("disabled"));
        });
    }

    [Fact]
    public async Task A_timeout_below_the_chat_is_reported_and_the_composer_works_again()
    {
        // What HttpClient throws when its Timeout or a handler's ConnectTimeout expires before the headers
        // arrive: a cancellation the chat never asked for. The second run succeeds.
        var transport = new ReplayTransport(await Recordings.LoadAsync(Recordings.WorkOrderStatus))
        {
            FailFirstRunWith = new TaskCanceledException("timeout", new TimeoutException()),
        };
        var chat = RenderChat(transport);

        Ask(chat, "What's the status of WO-1001?");

        chat.WaitForAssertion(() =>
        {
            Assert.Equal("Something went wrong.", chat.Find(".bd-status__text").TextContent);
            Assert.Equal("Beacon's agent didn't answer in time. Check that it's running, then try again.",
                chat.Find("[role=alert]").TextContent);
            Assert.False(chat.Find("input.bd-composer__input").HasAttribute("disabled"));
            Assert.False(chat.Find("button[type=submit]").HasAttribute("disabled"));
        });

        // The run was released, so the composer really works: the next question goes out and is answered.
        Ask(chat, "What's the status of WO-1001?");
        WaitForAnswers(chat, 1);
        Assert.Equal(2, transport.Inputs.Count);
        Assert.Empty(chat.FindAll("[role=alert]"));
    }

    [Fact]
    public async Task Hostile_markup_in_a_streamed_answer_never_reaches_the_DOM()
    {
        var transport = new ReplayTransport(await Recordings.LoadAsync(Recordings.HandoverNote));
        var chat = RenderChat(transport);

        Ask(chat, "Write a handover note for WO-1001");
        WaitForAnswers(chat, 1);

        Assert.Empty(chat.FindAll(".bd-transcript img, .bd-transcript script"));
        var link = Assert.Single(chat.FindAll(".bd-transcript a"));
        Assert.Equal("https://contoso.example/manuals/ca-36x", link.GetAttribute("href"));
        Assert.Equal("noopener noreferrer nofollow", link.GetAttribute("rel"));

        // The portal message is shown as text: the tag is visible and the javascript: link is just words.
        var quote = chat.Find(".bd-transcript blockquote").TextContent;
        Assert.Contains("<img src=\"x\" onerror=\"alert('portal')\">", quote);
        Assert.Contains("confirm the visit here", quote);
    }

    [Fact]
    public void Before_the_component_is_interactive_nothing_can_be_sent()
    {
        SetRendererInfo(new RendererInfo("Static", isInteractive: false));
        var chat = RenderChat(new ReplayTransport([]));

        Assert.True(chat.Find("input.bd-composer__input").HasAttribute("disabled"));
        Assert.True(chat.Find("button[type=submit]").HasAttribute("disabled"));
        Assert.All(chat.FindAll(".bd-suggestion"), button => Assert.True(button.HasAttribute("disabled")));
    }

    private IRenderedComponent<DeskChat> RenderChat(ReplayTransport transport)
    {
        _transport = transport;
        return Render<DeskChat>();
    }

    private void Ask(IRenderedComponent<DeskChat> chat, string text) =>
        // On the renderer's dispatcher, so no render (say, the previous run's last one) can land between
        // finding the elements and raising their events, which would leave a stale event handler ID.
        Renderer.Dispatcher.InvokeAsync(() =>
        {
            chat.Find("input.bd-composer__input").Input(text);
            chat.Find("form.bd-composer").Submit();
        }).GetAwaiter().GetResult();

    private static void WaitForAnswers(IRenderedComponent<DeskChat> chat, int count) =>
        chat.WaitForAssertion(() =>
        {
            Assert.Equal(count, chat.FindAll(".bd-message--assistant:not(.bd-message--streaming):not(.bd-message--pending)").Count);
            Assert.Equal("Ready", chat.Find(".bd-status__text").TextContent);
        });

    private static string StreamingText(IRenderedComponent<DeskChat> chat) =>
        chat.Find(".bd-message--streaming .bd-markdown").TextContent.Trim();
}
