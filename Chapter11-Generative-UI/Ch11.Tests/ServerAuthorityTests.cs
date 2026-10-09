using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Domain;
using Beacon.Testing;
using Ch11.Beacon.Agent.Components;
using Ch11.Beacon.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AgentProgram = Ch11.Beacon.Agent.Program;

namespace Ch11.Tests;

/// <summary>
/// The server as the authority over components: the schema, then its own data, then the allow-list.
/// The in-process agent runs the scripted model with no delays.
/// </summary>
public sealed class ServerAuthorityTests : IClassFixture<ServerAuthorityTests.AgentFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly AgentFactory _agent;

    public ServerAuthorityTests(AgentFactory agent) => _agent = agent;

    public sealed class AgentFactory : WebApplicationFactory<AgentProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .UseSetting("AI:ScriptedChunkDelayMilliseconds", "0")
            .UseSetting("Beacon:ArgumentFragmentDelayMilliseconds", "0");
    }

    [Fact]
    public async Task A_valid_card_streams_in_pieces_and_is_answered_with_the_servers_props_and_actions()
    {
        var frames = await AGUIWire.RunRawAsync(_agent.CreateClient(), "/agent", AGUIWire.UserRun("Which capacitor fits WO-1001?"), Ct);

        var start = frames.Single(f => Type(f) == "TOOL_CALL_START" && f.GetProperty("toolCallName").GetString() == "show_part_card");
        var callId = start.GetProperty("toolCallId").GetString();
        Assert.True(frames.Count(f => Type(f) == "TOOL_CALL_ARGS" && f.GetProperty("toolCallId").GetString() == callId) > 1);

        var result = Envelope(frames, callId!);
        Assert.Equal(("accepted", "PartCard"), (result.Status, result.Component));
        Assert.Equal("Dual run capacitor 45/5 uF", result.Props!.Value.GetProperty("name").GetString());
        var order = Assert.Single(result.Actions!);
        Assert.Equal(("order", true), (order.Name, order.Enabled));
        Assert.Equal("RUN_FINISHED", Type(frames[^1]));
        Assert.Empty(frames.SelectMany(f => AGUIWire.NullProperties(f)));
    }

    [Fact]
    public async Task Schema_violations_are_rejected_before_the_rules_run()
    {
        var rulesRan = false;
        var tool = new ComponentTool(BeaconComponents.PartCard, _ => { rulesRan = true; return ComponentResult.Rejected(null, ["unreachable"]); }, NullLogger.Instance);

        var result = await InvokeAsync(tool, """{"sku":"CAP-45-5","unitPrice":"cheap","inStock":24}""");

        Assert.False(rulesRan);
        Assert.Equal("rejected", result.Status);
        Assert.Equal(["$.name: is required.", "$.unitPrice: must be a number."], result.Errors);
    }

    [Fact]
    public async Task Facts_are_corrected_from_Beacons_records_and_unresolvable_references_are_rejected()
    {
        var rules = new ComponentRules(new InMemoryFieldServiceStore());
        var card = new ComponentTool(BeaconComponents.PartCard, rules.PartCard, NullLogger.Instance);

        // A stale price: the card shows the catalog's, and the model hears about it.
        var corrected = await InvokeAsync(card, """{"sku":"CAP-45-5","name":"Run capacitor","unitPrice":36.00,"inStock":24,"workOrderId":"WO-1001"}""");
        Assert.True(corrected.IsAccepted);
        Assert.Equal(38.50m, corrected.Props!.Value.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(["$.name: corrected to Dual run capacitor 45/5 uF from Beacon's records.", "$.unitPrice: corrected to 38.50 from Beacon's records."], corrected.Corrections);

        // A part that doesn't exist, and one that doesn't fit the unit on the work order.
        Assert.Equal(["$.sku: CAP-99 isn't a stocked part. Use a SKU from search_parts."],
            (await InvokeAsync(card, """{"sku":"CAP-99","name":"Run capacitor 99 uF","unitPrice":24,"inStock":6}""")).Errors);
        Assert.Equal(["$.sku: CMP-RT120 doesn't fit the CA-36X on WO-1001."],
            (await InvokeAsync(card, """{"sku":"CMP-RT120","name":"Compressor","unitPrice":4820,"inStock":1,"workOrderId":"WO-1001"}""")).Errors);

        // Over the spending threshold: the card is accepted, but its button isn't granted.
        var compressor = await InvokeAsync(card, """{"sku":"CMP-CA36","name":"Scroll compressor 3-ton","unitPrice":1795,"inStock":2,"workOrderId":"WO-1001"}""");
        Assert.Equal(new ComponentAction("order", "Order this part", Enabled: false, Reason: "Over $500: needs approval"), Assert.Single(compressor.Actions!));

        // A schedule slot and a fault that aren't on record.
        var timeline = new ComponentTool(BeaconComponents.ScheduleTimeline, rules.ScheduleTimeline, NullLogger.Instance);
        Assert.Equal(["$.lanes[0].slots[0]: TECH-1 has no WO-1003 from 09:00 to 11:00 on 2026-11-16."],
            (await InvokeAsync(timeline, """{"date":"2026-11-16","lanes":[{"technicianId":"TECH-1","slots":[{"start":"09:00","end":"11:00","workOrderId":"WO-1003"}]}]}""")).Errors);

        var chart = new ComponentTool(BeaconComponents.FaultHistoryChart, rules.FaultHistoryChart, NullLogger.Instance);
        Assert.Equal(["$.faults[1]: EQ-501 has no E9 fault on record for 2026-02-01."],
            (await InvokeAsync(chart, """{"equipmentId":"EQ-501","faults":[{"date":"2025-07-02","code":"E3"},{"date":"2026-02-01","code":"E9"}]}""")).Errors);
    }

    [Fact]
    public async Task A_component_nobody_offered_is_refused_on_the_server_and_the_model_recovers_in_the_same_run()
    {
        var frames = await AGUIWire.RunRawAsync(_agent.CreateClient(), "/agent", AGUIWire.UserRun("Show WO-1001 as an invoice"), Ct);

        var callId = frames.Single(f => Type(f) == "TOOL_CALL_START" && f.GetProperty("toolCallName").GetString() == "show_invoice_table")
            .GetProperty("toolCallId").GetString()!;
        var refusal = Envelope(frames, callId);
        Assert.Equal("rejected", refusal.Status);
        Assert.Null(refusal.Component);
        Assert.Equal(["show_part_card", "show_schedule_timeline", "show_fault_history_chart"], refusal.Available);

        // The call isn't left for the client: the run goes on to answer in text and finishes with nothing pending.
        Assert.Contains(frames, f => Type(f) == "TOOL_CALL_START" && f.GetProperty("toolCallName").GetString() == "get_work_order");
        Assert.Equal("TEXT_MESSAGE_END", Type(frames[^2]));
        Assert.False(frames[^1].GetProperty("outcome").TryGetProperty("pendingToolCallIds", out _));
    }

    [Fact]
    public async Task A_client_cannot_redefine_a_component_by_declaring_a_tool_with_its_name()
    {
        var model = new RecordedChatClient(RecordedChatClient.TextTurn("msg-1", "Hello."));
        using var agent = _agent.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddKeyedSingleton<IChatClient>(AgentProgram.ModelKey, model)));

        var input = AGUIWire.UserRun("Hello");
        input.Tools =
        [
            new AGUITool { Name = "show_part_card", Description = "Shows anything.", Parameters = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone() },
            new AGUITool { Name = "highlight_schedule_slot", Description = "A Desk tool.", Parameters = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone() },
        ];
        await AGUIWire.RunRawAsync(agent.CreateClient(), "/agent", input, Ct);

        var tools = model.Calls[0].Options!.Tools!;
        var card = Assert.IsType<ComponentTool>(Assert.Single(tools, t => t.Name == "show_part_card"));
        Assert.True(JsonElement.DeepEquals(BeaconComponents.PartCard.Schema, card.JsonSchema));
        Assert.Contains(tools, t => t.Name == "highlight_schedule_slot"); // the client's own tools still go through
    }

    private static async Task<ComponentResult> InvokeAsync(AIFunction tool, string props)
    {
        var arguments = JsonDocument.Parse(props).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        var result = (JsonElement)(await tool.InvokeAsync(new AIFunctionArguments(arguments), Ct))!;
        Assert.True(ComponentResult.TryParse(result.GetRawText(), out var envelope));
        return envelope;
    }

    private static ComponentResult Envelope(IReadOnlyList<JsonElement> frames, string callId)
    {
        var content = frames.Single(f => Type(f) == "TOOL_CALL_RESULT" && f.GetProperty("toolCallId").GetString() == callId).GetProperty("content").GetString();
        Assert.True(ComponentResult.TryParse(content, out var envelope));
        return envelope;
    }

    private static string? Type(JsonElement frame) => frame.GetProperty("type").GetString();
}
