using AGUI.Abstractions;
using Beacon.Client.Core;
using Bunit;
using Ch18.Beacon.Desk.Activity;
using Ch18.Beacon.Desk.Components;
using Ch18.Beacon.Desk.Components.Activity;
using Ch18.Tests.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ch18.Tests;

/// <summary>The Desk's specialist board and lanes, rendered with bUnit from views the agent really produced.</summary>
public sealed class LaneRenderingTests : BunitContext
{
    public LaneRenderingTests()
    {
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        Services.AddSingleton(Focus);
    }

    private ApprovalFocus Focus { get; } = new();

    // Services first: bUnit refuses new registrations once it has resolved anything, renderer info included.
    private void Use(AgentConversation conversation)
    {
        Services.AddSingleton(conversation);
        SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public async Task Each_invocation_gets_a_lane_and_every_entry_names_who_produced_it()
    {
        await using var host = new AgentHost();
        using var desk = new DeskStack(host);
        await desk.SendAsync(Runs.Capacitor);
        Use(desk.Conversation);

        var board = Render<ActivityBoard>();

        var lanes = board.FindAll(".ch18-lane");
        Assert.Equal(3, lanes.Count);
        foreach (var lane in lanes)
        {
            var label = lane.QuerySelector(".ch18-badge")!.TextContent;
            Assert.Equal("Done", lane.QuerySelector(".ch18-status")!.TextContent);

            // The lane's heading labels it for assistive technology, and every message and tool card repeats who.
            Assert.Equal(label, board.Find($"#{lane.GetAttribute("aria-labelledby")}").TextContent.Trim());
            var entries = lane.QuerySelectorAll(".ch18-entry");
            Assert.NotEmpty(entries);
            Assert.All(entries, entry => Assert.Equal(label, entry.QuerySelector(".ch18-by")!.TextContent));
        }

        Assert.Equal(["Planning", "Consulting", "Answering"], board.FindAll(".ch18-step").Select(s => s.FirstChild!.TextContent.Trim()));
        Assert.Contains("Parts specialist finished.", board.Find("[aria-live=polite]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_lane_says_why_in_words()
    {
        await using var host = new AgentHost();
        using var desk = new DeskStack(host);
        await desk.SendAsync(Runs.Rooftop);
        Use(desk.Conversation);

        var board = Render<ActivityBoard>();

        var failed = board.FindAll(".ch18-lane").Single(l => l.QuerySelector(".ch18-badge")!.TextContent == "Warranty specialist");
        Assert.Equal("Failed", failed.QuerySelector(".ch18-status")!.TextContent);
        Assert.Equal("The warranty specialist didn't answer within 1 second.", failed.QuerySelector(".ch18-lane__error")!.TextContent);
        Assert.Equal("failed", failed.QuerySelector(".ch18-tool__status")!.TextContent); // the abandoned lookup has a result that says so
    }

    [Fact]
    public async Task A_waiting_lane_offers_the_approval_and_the_buttons_answer_its_interrupt()
    {
        await using var host = new AgentHost();
        using var desk = new DeskStack(host);
        await desk.SendAsync(Runs.Compressor);
        var parts = SpecialistBoard.From(desk.Conversation.View).Runs.Single().Lanes.Single(l => l.Name == "parts-specialist");
        List<InterruptView> approved = [];

        SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("Server", isInteractive: true));
        var lane = Render<SpecialistLaneView>(p => p
            .Add(l => l.Lane, parts)
            .Add(l => l.OnApprove, (InterruptView i) => approved.Add(i)));

        Assert.Equal("Waiting for your approval", lane.Find(".ch18-status").TextContent);
        var group = lane.Find(".ch18-approval[role=group]");
        Assert.Contains("Order 1 x CMP-CA36", group.TextContent, StringComparison.Ordinal);
        Assert.Equal(2, group.GetAttribute("aria-labelledby")!.Split(' ').Length); // the lane's name and the request
        Assert.Equal("waiting for approval", lane.FindAll(".ch18-tool__status").Single(s => s.ParentElement!.TextContent.Contains("place_part_order", StringComparison.Ordinal)).TextContent);

        await lane.InvokeAsync(() => lane.FindAll("button").Single(b => b.TextContent == "Approve").Click());

        Assert.Equal(parts.OpenInterrupts.Single().Id, Assert.Single(approved).Id);
    }

    [Fact]
    public async Task The_chat_offers_a_keyboard_path_to_the_approval_on_the_board()
    {
        await using var host = new AgentHost();
        using var desk = new DeskStack(host);
        await desk.SendAsync(Runs.Compressor);
        Use(desk.Conversation);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var workspace = Render<DeskWorkspace>();

        // The composer is disabled while the approval waits; the button above it says where the approval is.
        Assert.True(workspace.Find(".bd-composer__input").HasAttribute("disabled"));
        var goTo = workspace.FindAll("button").Single(b => b.TextContent == "Go to the parts specialist's approval");

        // Each lane has a heading a screen reader can jump to, and the approval has its own, which takes focus.
        Assert.Equal(3, workspace.FindAll(".ch18-lane h3").Count);
        var heading = workspace.Find(".ch18-approval h4");
        Assert.Equal("-1", heading.GetAttribute("tabindex"));

        await workspace.InvokeAsync(() => goTo.Click());

        var focused = JSInterop.VerifyFocusAsyncInvoke();
        Assert.Equal(heading.GetAttribute("blazor:elementReference"), focused.Arguments[0] is ElementReference element ? element.Id : null);
    }

    [Fact]
    public async Task A_refused_answer_says_so_in_the_chat_and_the_approval_stays_on_the_board()
    {
        await using var host = new AgentHost();
        using var desk = new DeskStack(host);
        await desk.SendAsync(Runs.Compressor);
        var interrupt = desk.Conversation.View.OpenInterrupts.Single();
        await Runs.RawAsync(host.Client(), Runs.Resume("t-desk", "r-other", Runs.Compressor, Runs.SdkApproval(interrupt.Id, approved: true, interrupt.ToolCallId!)));
        await LaneApprovals.ApproveAsync(desk.Conversation, interrupt, Runs.Ct);
        Use(desk.Conversation);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var workspace = Render<DeskWorkspace>();

        Assert.Contains("refused the request (HTTP 409), so nothing ran", workspace.Markup, StringComparison.Ordinal);
        Assert.Single(workspace.FindAll(".ch18-approval"));
    }

    [Fact]
    public void Approval_buttons_wait_while_a_turn_runs()
    {
        var lane = new SpecialistLane("parts-1", "parts-specialist", "Parts specialist")
        {
            Status = LaneStatus.WaitingForApproval,
            OpenInterrupts = [new InterruptView("apr_1", InterruptKind.ToolApproval) { Message = "Order 1 x CMP-CA36.", SubagentRunId = "parts-1" }],
        };

        SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("Server", isInteractive: true));
        var rendered = Render<SpecialistLaneView>(p => p.Add(l => l.Lane, lane).Add(l => l.Busy, true));

        Assert.All(rendered.FindAll(".ch18-approval button"), b => Assert.True(b.HasAttribute("disabled")));
    }
}
