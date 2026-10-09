using System.Text.Json;
using Beacon.Client.Core;
using Ch09.Beacon.ToolCards;
using Ch09.Beacon.ToolCards.Maui;
using Microsoft.Maui.Controls;

namespace Ch09.Tests;

/// <summary>
/// Beacon Field's half of section 9.4: the card models the Desk renders choose a .NET MAUI template, and
/// anything without a card of its own falls back to the status chip.
/// </summary>
public sealed class FieldCardTemplateTests
{
    private static readonly ToolCardTemplateSelector Selector = new()
    {
        Chip = new DataTemplate(),
        WorkOrder = new DataTemplate(),
        Parts = new DataTemplate(),
        Warranty = new DataTemplate(),
        Checklist = new DataTemplate(),
        Text = new DataTemplate(),
    };

    [Fact]
    public void Each_result_with_a_card_gets_its_own_template()
    {
        var warranty = ToolCards.From(Completed("check_warranty", """{"equipmentId":"EQ-501"}""",
            """{"equipmentId":"EQ-501","covered":true,"expiresOn":"2029-05-14","coverage":"Parts: 10 years"}"""));
        var parts = ToolCards.From(Completed("search_parts", """{"query":"","model":"CA-36X"}""",
            """[{"sku":"CAP-45-5","name":"Dual run capacitor 45/5 uF","unitPrice":38.5,"inStock":24,"warehouse":"Portsmouth"}]"""));
        var checklist = ToolCards.From(Completed("set_checklist_item", """{"workOrderId":"WO-1001","itemId":"C3","done":true}""",
            """{"workOrderId":"WO-1001","itemId":"C3","text":"Measure the run capacitor","done":true,"changed":false,"doneCount":1,"itemCount":4}"""));

        Assert.Same(Selector.Warranty, Selector.SelectTemplate(warranty, null));
        Assert.Same(Selector.Parts, Selector.SelectTemplate(parts, null));
        Assert.Same(Selector.Checklist, Selector.SelectTemplate(checklist, null));
        Assert.Same(Selector.WorkOrder, Selector.SelectTemplate(new ToolCard("call-1", "get_work_order", ToolCardState.Succeeded, "Looked up WO-1001")
        {
            Body = new WorkOrderBody("WO-1001", "Scheduled", "High", "Air conditioner short-cycling", "Harbor View Dental", "EQ-501", "Contoso Air CA-36X", null, "TECH-1", 1, 4),
        }, null));
    }

    [Fact]
    public void A_call_without_a_card_shows_the_chip_alone()
    {
        var running = ToolCards.From(new ToolCallView("call-2", "get_work_order")
        {
            Status = ToolCallStatus.InProgress,
            ArgumentsText = """{"workOrderId":"WO-1005"}""",
        });
        var failed = ToolCards.From(Completed("check_warranty", """{"equipmentId":"EQ-701"}""",
            """{"error":"The warranty service did not answer, so this lookup failed. This is usually temporary. (Reference 16B566)"}"""));
        var generic = ToolCards.From(Completed("check_warranty", """{"equipmentId":"EQ-701"}""", "Error: Function failed."));

        Assert.Same(Selector.Chip, Selector.SelectTemplate(running, null));
        Assert.Same(Selector.Chip, Selector.SelectTemplate(failed, null));
        Assert.Equal("Checking the warranty on EQ-701 failed", failed.Headline);
        Assert.Same(Selector.Text, Selector.SelectTemplate(generic, null));
    }

    private static ToolCallView Completed(string name, string arguments, string result)
    {
        using var document = JsonDocument.Parse(arguments);
        return new("call-" + Guid.NewGuid().ToString("N"), name)
        {
            Status = ToolCallStatus.Completed,
            ArgumentsText = arguments,
            Arguments = document.RootElement.Clone(),
            Result = result,
        };
    }
}
