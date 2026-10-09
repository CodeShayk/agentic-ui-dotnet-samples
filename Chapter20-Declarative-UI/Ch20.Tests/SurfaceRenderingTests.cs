using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Bunit;
using Ch20.Beacon.Desk.Components.Chat;
using Ch20.Beacon.Desk.Components.Surfaces;
using Ch20.DeclarativeUi;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static Ch20.Tests.Surfaces;

namespace Ch20.Tests;

/// <summary>
/// Beacon Desk's renderer with bUnit, on surfaces the agent actually sent: labeled controls, two-way
/// binding, checks, the degraded placeholder, the rejection notice, text that never becomes markup, and the
/// chat sending a form's action from a replayed stream.
/// </summary>
public sealed class SurfaceRenderingTests : BunitContext
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 16, 16, 52, 7, TimeSpan.Zero);

    public SurfaceRenderingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose; // the composer calls FocusAsync
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public void Every_control_is_labeled_and_the_submit_button_says_why_it_is_disabled()
    {
        var view = RenderSurface(Instance(Recordings.Operations(Recordings.Wo1003)));

        Assert.Equal("HP-24S heat pump inspection for WO-1003", view.Find("section.ch20-surface").GetAttribute("aria-label"));
        Assert.Equal("HP-24S heat pump inspection for WO-1003", view.Find("h3.ch20-text--title").TextContent);
        var inputs = view.FindAll("input, textarea");
        Assert.Equal(12, inputs.Count); // 5 checks, 3 readings, 3 conditions, notes
        foreach (var input in inputs.Where(i => i.GetAttribute("type") != "radio"))
        {
            Assert.NotNull(view.Find($"label[for='{input.Id}']"));
        }

        Assert.Equal("Overall condition", view.Find("fieldset.ch20-choices legend").TextContent);
        var submit = view.Find("button.bd-button--primary");
        Assert.Equal("Submit inspection", submit.TextContent.Trim());
        Assert.True(submit.HasAttribute("disabled"));
        Assert.Equal("Enter every reading and choose the overall condition.", view.Find($"#{submit.GetAttribute("aria-describedby")}").TextContent);
    }

    [Fact]
    public void Typing_updates_the_data_model_and_a_failed_check_shows_only_once_the_field_is_touched()
    {
        var surface = Instance(Recordings.Operations(Recordings.Wo1003));
        var view = RenderSurface(surface);
        Assert.Empty(view.FindAll(".ch20-error"));

        view.Find("input[inputmode=decimal]").Input("23");

        var input = view.Find("input[inputmode=decimal]");
        Assert.Equal("true", input.GetAttribute("aria-invalid"));
        Assert.Equal("Enter a number from 0 to 10 A.", view.Find($"#{input.GetAttribute("aria-describedby")}").TextContent);
        Assert.Equal("23", surface.Data!.Text(JsonNode.Parse("""{"path":"/readings/fan_amps"}"""), DataPointer.Root));
    }

    [Fact]
    public void A_filled_form_enables_submit_and_pressing_it_raises_the_action()
    {
        SurfaceActionRequest? pressed = null;
        var view = RenderSurface(Instance(Recordings.Operations(Recordings.Wo1003)), request => pressed = request);

        var checkboxes = view.FindAll("input[type=checkbox]");
        foreach (var id in checkboxes.Skip(1).Select(c => c.Id).ToList())
        {
            view.Find($"#{id}").Change(true);
        }

        var readings = view.FindAll("input[inputmode=decimal]").Select(i => i.Id).ToList();
        view.Find($"#{readings[0]}").Input("2.3");
        view.Find($"#{readings[1]}").Input("10.2");
        view.Find($"#{readings[2]}").Input("118");
        view.Find("input[type=radio][id$='-follow_up']").Change("on");
        view.Find("textarea").Input("Grinding from the fan motor at start-up.");

        var submit = view.Find("button.bd-button--primary");
        Assert.False(submit.HasAttribute("disabled"));
        submit.Click();

        Assert.NotNull(pressed);
        var action = pressed.Surface.CreateAction(pressed.Button, Monday)!;
        Assert.Equal(new[] { "follow_up" }, ((JsonArray)action.Context["condition"]!).Select(v => v!.GetValue<string>()));
        Assert.Equal("118", action.Context["readings"]!["suction_psig"]!.GetValue<string>());
    }

    [Fact]
    public void A_component_this_Desk_cannot_draw_becomes_a_labeled_placeholder_and_the_rest_works()
    {
        var view = RenderSurface(Instance(Recordings.Operations(Recordings.Wo1005)));

        Assert.Equal("degraded", view.Find("section.ch20-surface").GetAttribute("data-outcome"));
        Assert.Contains("needs a newer Beacon Desk", view.Find(".ch20-surface__notice").TextContent, StringComparison.Ordinal);
        var placeholder = view.Find(".ch20-degraded");
        Assert.Equal("Slider", placeholder.GetAttribute("data-component"));
        Assert.Equal("Compressor vibration (0 none to 5 severe)", placeholder.QuerySelector(".ch20-degraded__label")!.TextContent);
        Assert.Equal("Current value: 0", placeholder.QuerySelector(".ch20-degraded__value")!.TextContent);
        Assert.Equal(2, view.FindAll("input[inputmode=decimal]").Count); // the other readings still render
        Assert.Equal(4, view.FindAll("input[type=checkbox]").Count);
    }

    [Fact]
    public void A_rejected_surface_draws_nothing_but_a_notice()
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        ((JsonArray)Component(operations, "form")["children"]!).Add(JsonValue.Create("root")); // a cycle

        var view = RenderSurface(Instance(operations));

        Assert.Equal("Beacon sent a form this Desk won't show.", view.Find(".ch20-surface--rejected .ch20-surface__title").TextContent);
        Assert.Contains("child_cycle", view.Find(".ch20-surface__detail").TextContent, StringComparison.Ordinal);
        Assert.Empty(view.FindAll("input, textarea, button"));
    }

    [Fact]
    public void Text_from_the_agent_is_rendered_as_text_and_never_as_markup()
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        Component(operations, "title")["text"] = "<script>alert('x')</script><b>Inspection</b>";

        var view = RenderSurface(Instance(operations));

        Assert.Empty(view.FindAll("script"));
        Assert.Empty(view.FindAll("b"));
        Assert.Equal("<script>alert('x')</script><b>Inspection</b>", view.Find("h3.ch20-text--title").TextContent);
    }

    [Fact]
    public void While_Beacon_is_still_answering_the_submit_button_waits_and_says_why()
    {
        var surface = Instance(Recordings.Operations(Recordings.Wo1003));
        DataBindingTests.Fill(surface);

        var busy = Render<SurfaceView>(p => p.Add(v => v.Surface, surface).Add(v => v.Busy, true));
        var button = busy.Find("button.bd-button--primary");
        Assert.True(button.HasAttribute("disabled"));
        Assert.Equal("Wait until Beacon finishes answering.", busy.Find($"#{button.Id}-why").TextContent);

        var ready = Render<SurfaceView>(p => p.Add(v => v.Surface, surface).Add(v => v.Busy, false));
        Assert.False(ready.Find("button.bd-button--primary").HasAttribute("disabled"));
    }

    [Fact]
    public void A_submitted_form_is_read_only()
    {
        var surface = Instance(Recordings.Operations(Recordings.Wo1003));
        surface.MarkSubmitted();

        var view = RenderSurface(surface);

        Assert.True(view.Find("fieldset.ch20-surface__body").HasAttribute("disabled"));
        Assert.Equal("Submitted to Beacon.", view.Find(".ch20-surface__status").TextContent);
    }

    private IRenderedComponent<SurfaceView> RenderSurface(SurfaceInstance surface, Action<SurfaceActionRequest>? onAction = null) =>
        Render<SurfaceView>(p => p
            .Add(v => v.Surface, surface)
            .Add(v => v.OnAction, (SurfaceActionRequest request) => onAction?.Invoke(request)));
}

/// <summary>
/// Beacon Desk's chat over two recorded runs: the form appears where its activity arrived, and pressing
/// Submit sends the next run with the action in forwardedProps and no new user message.
/// </summary>
public sealed class DeskChatTests : BunitContext
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 16, 16, 52, 7, TimeSpan.Zero);
    private ReplayTransport? _transport;

    public DeskChatTests()
    {
        Services.AddLogging();
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Monday));
        Services.AddSingleton(_ => new WireEventTap(_transport ?? throw new InvalidOperationException("Set the transport before rendering.")));
        Services.AddSingleton<IChatClient>(s => new AGUIChatClient(new AGUIChatClientOptions { Transport = s.GetRequiredService<WireEventTap>() }));
        Services.AddSingleton(s => new AgentConversation(s.GetRequiredService<IChatClient>(), new ConversationOptions { Tap = s.GetRequiredService<WireEventTap>() }));
        Services.AddSingleton(new SurfaceBoard(DeskCatalog.CreateValidator()));
        JSInterop.Mode = JSRuntimeMode.Loose; // the composer calls FocusAsync
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public async Task The_chat_draws_the_form_where_it_arrived_and_sends_its_action_in_the_next_runs_forwardedProps()
    {
        var transport = _transport = new ReplayTransport(await Recordings.LoadAsync(Recordings.Wo1003), await Recordings.LoadAsync(Recordings.Submit));
        var chat = Render<DeskChat>();
        await chat.InvokeAsync(() => chat.FindAll("button.bd-suggestion").First(b => b.TextContent.Contains("WO-1003", StringComparison.Ordinal)).Click());

        chat.WaitForAssertion(() =>
        {
            Assert.NotNull(chat.Find(".bd-message section.ch20-surface[data-outcome=accepted]"));
            Assert.Equal("Ready", chat.Find(".bd-status__text").TextContent);
        });

        await chat.InvokeAsync(() =>
        {
            foreach (var id in chat.FindAll("input[type=checkbox]").Skip(1).Select(c => c.Id).ToList())
            {
                chat.Find($"#{id}").Change(true);
            }

            var readings = chat.FindAll("input[inputmode=decimal]").Select(i => i.Id).ToList();
            chat.Find($"#{readings[0]}").Input("2.3");
            chat.Find($"#{readings[1]}").Input("10.2");
            chat.Find($"#{readings[2]}").Input("118");
            chat.Find("input[type=radio][id$='-follow_up']").Change("on");
            chat.Find("textarea").Input("Grinding from the fan motor at start-up.");
        });

        await chat.InvokeAsync(() => chat.Find("button.bd-button--primary").Click());

        chat.WaitForAssertion(() =>
        {
            Assert.Equal("Submitted to Beacon.", chat.Find(".ch20-surface__status").TextContent);
            Assert.StartsWith("Recorded.", chat.FindAll(".bd-message").Last().TextContent.Replace("Beacon", string.Empty, StringComparison.Ordinal).Trim(), StringComparison.Ordinal);
        });

        // The second run carried the action and no new user message; the activity never went back.
        var submit = transport.Inputs[1];
        var userAction = submit.ForwardedProperties!.Value.GetProperty("a2uiAction").GetProperty("userAction");
        Assert.Equal("submit_inspection", userAction.GetProperty("name").GetString());
        Assert.Equal("inspection-WO-1003", userAction.GetProperty("surfaceId").GetString());
        Assert.Equal("2026-11-16T16:52:07.000Z", userAction.GetProperty("timestamp").GetString());
        Assert.Equal("10.2", userAction.GetProperty("context").GetProperty("readings").GetProperty("defrost_kohm").GetString());
        Assert.Equal(submit.Messages.Count(m => m is AGUIUserMessage), transport.Inputs[0].Messages.Count(m => m is AGUIUserMessage));
        Assert.DoesNotContain(submit.Messages, m => m is AGUIActivityMessage);
    }

    [Fact]
    public async Task A_form_the_agent_refuses_reopens_with_its_values_and_says_why()
    {
        _transport = new ReplayTransport(await Recordings.LoadAsync(Recordings.Wo1003)) { Refusals = { [2] = HttpStatusCode.Conflict } };
        var chat = Render<DeskChat>();
        await chat.InvokeAsync(() => chat.FindAll("button.bd-suggestion").First(b => b.TextContent.Contains("WO-1003", StringComparison.Ordinal)).Click());
        chat.WaitForAssertion(() => Assert.Equal("Ready", chat.Find(".bd-status__text").TextContent));

        await chat.InvokeAsync(() =>
        {
            var readings = chat.FindAll("input[inputmode=decimal]").Select(i => i.Id).ToList();
            chat.Find($"#{readings[0]}").Input("2.3");
            chat.Find($"#{readings[1]}").Input("10.2");
            chat.Find($"#{readings[2]}").Input("118");
            chat.Find("input[type=radio][id$='-follow_up']").Change("on");
        });
        await chat.InvokeAsync(() => chat.Find("button.bd-button--primary").Click());

        chat.WaitForAssertion(() =>
        {
            Assert.Equal(DeskChat.FormErrorOf(new RunFailure(FailureKind.Rejected, "refused") { StatusCode = HttpStatusCode.Conflict }),
                chat.Find(".bd-status__error").TextContent);
            Assert.Equal("open", chat.Find("section.ch20-surface").GetAttribute("data-status"));
            Assert.Equal("2.3", chat.FindAll("input[inputmode=decimal]")[0].GetAttribute("value"));
        });
    }

    [Theory]
    [InlineData(FailureKind.Rejected, HttpStatusCode.BadRequest, "Beacon didn't accept these values.")]
    [InlineData(FailureKind.Rejected, HttpStatusCode.Conflict, "Beacon won't take this form again")]
    [InlineData(FailureKind.Unreachable, null, "Beacon couldn't take the form just now.")]
    public void A_refused_form_and_an_unreachable_agent_read_differently(FailureKind kind, HttpStatusCode? status, string start) =>
        Assert.StartsWith(start, DeskChat.FormErrorOf(new RunFailure(kind, "failed") { StatusCode = status }), StringComparison.Ordinal);
}

/// <summary>An <see cref="IAGUITransport"/> that answers each run with the next recorded stream.</summary>
public sealed class ReplayTransport(params IReadOnlyList<BaseEvent>[] runs) : IAGUITransport
{
    private int _next;

    public List<RunAgentInput> Inputs { get; } = [];

    /// <summary>Runs (counted from 1) the agent refuses with a 4xx before streaming, as its gates do.</summary>
    public Dictionary<int, HttpStatusCode> Refusals { get; } = [];

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Inputs.Add(input);
        if (Refusals.TryGetValue(Inputs.Count, out var status))
        {
            throw new HttpRequestException($"Response status code does not indicate success: {(int)status}.", null, status);
        }

        if (_next >= runs.Length)
        {
            throw new InvalidOperationException($"No recorded run left for run {Inputs.Count}.");
        }

        foreach (var evt in runs[_next++])
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return evt;
        }
    }
}
