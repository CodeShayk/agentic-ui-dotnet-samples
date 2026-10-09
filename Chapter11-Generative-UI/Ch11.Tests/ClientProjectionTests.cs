using System.Text.Json;
using Beacon.Client.Core;
using Ch11.Beacon.Components;
using Ch11.Beacon.ComponentViews;
using Ch11.Beacon.Desk.Components.Generative;

namespace Ch11.Tests;

/// <summary>
/// The client side without a UI: recorded streams replayed through the real client stack, projected
/// into generated components, and the Desk's registry that maps them to Blazor components.
/// </summary>
public sealed class ClientProjectionTests
{
    [Fact]
    public async Task A_recorded_card_moves_from_draft_through_checking_to_ready()
    {
        using var harness = await Harness.ReplayAsync(Recordings.PartCard, "Which capacitor fits WO-1001?");
        var projector = new ComponentProjector(ComponentKinds.All);

        // Every view the conversation published, projected the way the Desk projects them while rendering.
        var card = harness.Published
            .Select(v => v.ToolCalls.FirstOrDefault(c => c.Name == "show_part_card"))
            .OfType<ToolCallView>()
            .Select(projector.Project)
            .OfType<GeneratedComponent>()
            .ToList();

        Assert.Equal([ComponentPhase.Drafting, ComponentPhase.Validating, ComponentPhase.Ready], card.Select(c => c.Phase).Distinct());

        // While drafting the card filled in field by field, and offered nothing to press.
        var drafts = card.Where(c => c.Phase == ComponentPhase.Drafting).Select(c => c.View).OfType<PartCardView>().ToList();
        Assert.Contains(drafts, d => d is { Sku: "CAP-45-5", Price: null });
        Assert.Contains(drafts, d => d is { Price: "$38.50", Note: null });
        Assert.All(card.Where(c => c.IsDraft), c => Assert.Empty(c.Actions));

        var ready = card[^1];
        Assert.Equal("Part card: CAP-45-5, Dual run capacitor 45/5 uF, $38.50, 24 in stock at Portsmouth, for WO-1001.", ready.Describe());
        Assert.Equal(new ComponentAction("order", "Order this part", Enabled: true), Assert.Single(ready.Actions));

        // The lookup before it is an ordinary tool call: the Desk shows it as activity, not as a component.
        Assert.Null(projector.Project(harness.Conversation.View.ToolCalls.Single(c => c.Name == "search_parts")));
    }

    [Fact]
    public async Task Without_the_tap_the_card_appears_only_when_it_is_finished()
    {
        // AGUIChatClient holds a tool call back until its result arrives, so there is nothing to draft from:
        // the complete call and its result land back to back, and the first view is already the whole card.
        using var harness = await Harness.ReplayAsync(Recordings.PartCard, "Which capacitor fits WO-1001?", tap: false);
        var projector = new ComponentProjector(ComponentKinds.All);

        var phases = harness.Published
            .Select(v => v.ToolCalls.FirstOrDefault(c => c.Name == "show_part_card"))
            .OfType<ToolCallView>()
            .Select(projector.Project)
            .OfType<GeneratedComponent>()
            .Select(c => c.Phase)
            .Distinct();

        Assert.Equal([ComponentPhase.Validating, ComponentPhase.Ready], phases);
    }

    [Fact]
    public async Task A_rejected_card_and_an_unknown_component_settle_as_fallbacks_with_the_servers_reasons()
    {
        using var invalid = await Harness.ReplayAsync(Recordings.InvalidCard, "Show me a card for part CAP-99");
        var projector = new ComponentProjector(ComponentKinds.All);
        var card = projector.Project(invalid.Conversation.View.ToolCalls.Single())!;
        Assert.Equal((ComponentPhase.Rejected, true), (card.Phase, card.IsKnown));
        Assert.Equal("Beacon couldn't show this part card: CAP-99 isn't a stocked part. Use a SKU from search_parts.", card.Describe());

        using var unknown = await Harness.ReplayAsync(Recordings.UnknownComponent, "Show WO-1001 as an invoice");
        var invoice = projector.Project(unknown.Conversation.View.ToolCalls.Single(c => c.Name == "show_invoice_table"))!;
        Assert.Equal((ComponentPhase.Rejected, false), (invoice.Phase, invoice.IsKnown));
        Assert.StartsWith("show_invoice_table isn't a tool or component Beacon offers.", invoice.Problems[0]);
    }

    [Fact]
    public void Accepted_props_that_fail_this_clients_schema_are_not_rendered()
    {
        // A server (another build, another vendor) accepted props this client's contract forbids.
        var result = ComponentResult.Accepted("PartCard", JsonDocument.Parse("""{"sku":"CAP-45-5","name":"Capacitor","unitPrice":38.5,"inStock":24,"html":"<b>x</b>"}""").RootElement.Clone());
        var call = new ToolCallView("call-1", "show_part_card") { Status = ToolCallStatus.Completed, Result = result.ToJsonElement().GetRawText() };

        var component = new ComponentProjector(ComponentKinds.All).Project(call)!;

        Assert.Equal(ComponentPhase.Rejected, component.Phase);
        Assert.Null(component.View);
        Assert.Equal(["The props the agent confirmed don't match this component's schema.", "$: has a property the schema doesn't allow."], component.Problems);
    }

    [Fact]
    public void A_stopped_draft_keeps_what_it_showed_but_offers_nothing()
    {
        var projector = new ComponentProjector(ComponentKinds.All);
        var streaming = new ToolCallView("call-1", "show_part_card") { ArgumentsText = """{"sku":"CAP-45-5","name":"Dual run""" };
        Assert.Equal("Dual run", ((PartCardView)projector.Project(streaming)!.View!).Name);

        var stopped = projector.Project(streaming with { Status = ToolCallStatus.Abandoned })!;

        Assert.Equal(ComponentPhase.Abandoned, stopped.Phase);
        Assert.Equal("Dual run", ((PartCardView)stopped.View!).Name);
        Assert.Null(ComponentActionMessages.Compose(new ComponentActionRequest(stopped, "order")));
    }

    [Fact]
    public void The_registry_resolves_names_to_typed_components_and_refuses_a_mismatched_view()
    {
        var registry = ComponentRegistry.CreateDefault();

        Assert.True(registry.TryResolve("show_part_card", out var card));
        Assert.Equal((typeof(PartCard), typeof(PartCardView)), (card.ComponentType, card.Kind.ViewType));
        Assert.True(registry.TryResolve("show_fault_history_chart", out var chart));
        Assert.Equal(typeof(FaultHistoryChart), chart.ComponentType);
        Assert.False(registry.TryResolve("show_invoice_table", out _));
        Assert.False(registry.CreateProjector().Knows("show_invoice_table"));

        // A component drawing PartCardView can't be registered for the timeline's view model.
        Assert.Throws<ArgumentException>(() => new ComponentRegistry().Register<PartCard, PartCardView>(ComponentKinds.ScheduleTimeline));
    }

    [Fact]
    public void The_registry_passes_the_parameters_every_generated_component_declares()
    {
        Assert.True(ComponentRegistry.CreateDefault().TryResolve("show_part_card", out var registration));
        var component = new GeneratedComponent("call-1", "show_part_card") { View = new PartCardView { Sku = "CAP-45-5" } };

        var parameters = registration.ParametersFor(component, default);

        Assert.Equal(["View", "Component", "OnAction"], parameters.Keys);
        Assert.All(parameters.Keys, name => Assert.NotNull(typeof(PartCard).GetProperty(name)));
    }

    [Fact]
    public async Task The_shared_view_models_lay_out_the_timeline_and_the_chart_for_any_platform()
    {
        using var schedule = await Harness.ReplayAsync(Recordings.Schedule, "Show today's schedule");
        var projector = new ComponentProjector(ComponentKinds.All);
        var timeline = (ScheduleTimelineView)projector.Project(schedule.Conversation.View.ToolCalls.Single(c => c.Name == "show_schedule_timeline"))!.View!;

        // 7:00 to 18:00; Priya's 9:00-11:00 job starts 2/11 of the way along and is 2/11 wide.
        Assert.Equal(["7:00", "9:00", "11:00", "13:00", "15:00", "17:00"], timeline.Ticks.Select(t => t.Label));
        var priya = timeline.Lanes[0];
        Assert.Equal("Priya Raman", priya.Name); // the server filled in the name the model left out
        Assert.Equal((2 / 11.0, 2 / 11.0, "WO-1001", false), (priya.Blocks[0].Offset, priya.Blocks[0].Width, priya.Blocks[0].Label, priya.Blocks[0].IsFree), new TupleTolerance());
        Assert.True(priya.Blocks[1].IsFree);

        using var faults = await Harness.ReplayAsync(Recordings.Faults, "Chart the fault history for EQ-501");
        var chart = (FaultHistoryChartView)projector.Project(faults.Conversation.View.ToolCalls.Single(c => c.Name == "show_fault_history_chart"))!.View!;
        Assert.Equal(["E3", "E5"], chart.Rows);
        Assert.Equal([0, 0, 1], chart.Markers.Select(m => m.Row));
        Assert.True(chart.Markers.Select(m => m.X).SequenceEqual(chart.Markers.Select(m => m.X).Order()));
        Assert.Equal(("E5", "November 14, 2026"), (chart.Latest!.Code, chart.Latest.DateLabel));
        Assert.Equal("Jul 2025", chart.Ticks[0].Label);
    }

    private sealed class TupleTolerance : IEqualityComparer<(double, double, string, bool)>
    {
        public bool Equals((double, double, string, bool) x, (double, double, string, bool) y) =>
            Math.Abs(x.Item1 - y.Item1) < 1e-9 && Math.Abs(x.Item2 - y.Item2) < 1e-9 && x.Item3 == y.Item3 && x.Item4 == y.Item4;

        public int GetHashCode((double, double, string, bool) obj) => obj.Item3.GetHashCode();
    }
}
