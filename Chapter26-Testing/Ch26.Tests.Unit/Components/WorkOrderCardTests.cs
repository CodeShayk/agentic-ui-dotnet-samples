using System.Text.Json.Nodes;
using Bunit;
using Ch26.Beacon.Desk.Workspace;

namespace Ch26.Tests.Unit.Components;

/// <summary>A presentational component takes parameters, so its test is a render and a look.</summary>
public sealed class WorkOrderCardTests : BunitContext
{
    [Fact]
    public void Without_shared_state_the_card_invites_a_question()
    {
        var card = Render<WorkOrderCard>();

        Assert.Equal("Dispatch", card.Find("h1").TextContent);
        Assert.Empty(card.FindAll("dl"));
    }

    [Fact]
    public void The_active_work_order_renders_from_state()
    {
        var card = Render<WorkOrderCard>(p => p.Add(c => c.State, JsonNode.Parse("""
            {"activeWorkOrder":{"id":"WO-1001","status":"Scheduled","priority":"High",
             "summary":"Air conditioner short-cycling and blowing warm air","customer":"Harbor View Dental",
             "equipmentId":"EQ-501","equipment":"Contoso Air CA-36X","technicianId":"TECH-1"}}
            """)));

        Assert.Equal("WO-1001", card.Find("h1").TextContent);
        Assert.Equal(
            ["Scheduled", "High", "Harbor View Dental", "Contoso Air CA-36X (EQ-501)", "TECH-1"],
            card.FindAll("dd").Select(dd => dd.TextContent));
        Assert.Equal(card.Find("h1").Id, card.Find("section").GetAttribute("aria-labelledby"));
    }

    [Fact]
    public void State_is_text_never_markup()
    {
        // State comes from the agent, and the agent's tools read data a customer can write.
        var card = Render<WorkOrderCard>(p => p.Add(c => c.State, JsonNode.Parse("""
            {"activeWorkOrder":{"id":"WO-1001","summary":"<img src=x onerror=alert(1)>Broken unit"}}
            """)));

        Assert.Empty(card.FindAll("img"));
        Assert.Equal("<img src=x onerror=alert(1)>Broken unit", card.Find(".ch26-workorder__summary").TextContent);
    }
}
