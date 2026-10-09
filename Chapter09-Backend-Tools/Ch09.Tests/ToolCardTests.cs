using System.Text.Json;
using Beacon.Client.Core;
using Ch09.Beacon.ToolCards;

namespace Ch09.Tests;

/// <summary>
/// The platform-independent card models: previews from partial argument text, headlines by status,
/// failure detection from the result's shape, and defensive reading of untrusted results.
/// </summary>
public sealed class ToolCardTests
{
    [Theory]
    [InlineData("", null)]
    [InlineData("{\"work", null)]
    [InlineData("{\"workOrderId\":", null)]
    [InlineData("{\"workOrderId\":\"", "")]
    [InlineData("{\"workOrderId\":\"WO-10", "WO-10")]
    [InlineData("{\"workOrderId\":\"WO-1001\"", "WO-1001")]
    [InlineData("{\"workOrderId\":\"WO-1001\"}", "WO-1001")]
    public void A_preview_reads_whatever_the_argument_prefix_holds(string text, string? expected) =>
        Assert.Equal(expected, PartialArguments.Read(text).GetValueOrDefault("workOrderId"));

    [Fact]
    public void A_preview_decodes_escapes_and_survives_text_that_is_not_JSON()
    {
        Assert.Equal("run \"45/5\" cap", PartialArguments.Read("{\"query\":\"run \\\"45/5\\\" cap").GetValueOrDefault("query"));
        Assert.Equal("CA-36X", PartialArguments.Read("{\"query\":\"\",\"model\":\"CA-36X\"}").GetValueOrDefault("model"));
        Assert.Equal("true", PartialArguments.Read("{\"done\":true,").GetValueOrDefault("done"));
        Assert.Empty(PartialArguments.Read("not json at all"));
        Assert.Equal("WO-1001", PartialArguments.Read("{\"workOrderId\":\"WO-1001\", oops").GetValueOrDefault("workOrderId"));
    }

    [Fact]
    public void The_headline_follows_the_call_from_preview_to_result()
    {
        var call = new ToolCallView("call-1", "get_work_order") { ArgumentsText = "{\"workOrderId\":\"WO-10" };
        Assert.Equal((ToolCardState.Preparing, "Looking up WO-10…"), Headline(call));

        call = call with { Status = ToolCallStatus.InProgress, ArgumentsText = "{\"workOrderId\":\"WO-1001\"}", Arguments = Json("{\"workOrderId\":\"WO-1001\"}") };
        Assert.Equal((ToolCardState.Running, "Looking up WO-1001…"), Headline(call));

        Assert.Equal((ToolCardState.Stopped, "Looking up WO-1001: stopped before it finished"), Headline(call with { Status = ToolCallStatus.Abandoned }));

        var done = ToolCards.From(call with { Status = ToolCallStatus.Completed, Result = WorkOrderResult });
        Assert.Equal((ToolCardState.Succeeded, "Looked up WO-1001"), (done.State, done.Headline));
        var order = Assert.IsType<WorkOrderBody>(done.Body);
        Assert.Equal(("WO-1001", "Scheduled", "Harbor View Dental", "Contoso Air CA-36X", 1, 4),
            (order.Id, order.Status, order.Customer, order.Equipment, order.ChecklistDone, order.ChecklistTotal));
    }

    [Fact]
    public void An_error_result_fails_the_card_with_the_errors_words()
    {
        var card = ToolCards.From(Completed("check_warranty", "{\"equipmentId\":\"EQ-701\"}",
            "{\"error\":\"The warranty service did not answer, so this lookup failed. This is usually temporary. (Reference 16B566)\"}"));

        Assert.Equal(ToolCardState.Failed, card.State);
        Assert.Equal("Checking the warranty on EQ-701 failed", card.Headline);
        Assert.EndsWith("(Reference 16B566)", card.Error);
        Assert.Null(card.Body);
    }

    [Fact]
    public void Function_invocations_generic_error_text_looks_like_success_to_a_client()
    {
        // What the Desk gets when the server lets FunctionInvokingChatClient handle the exception:
        // a string, not Beacon's error shape, so the card can only show it as text.
        var card = ToolCards.From(Completed("check_warranty", "{\"equipmentId\":\"EQ-701\"}", "Error: Function failed."));

        Assert.Equal(ToolCardState.Succeeded, card.State);
        Assert.Equal("Error: Function failed.", Assert.IsType<TextBody>(card.Body).Text);
    }

    [Fact]
    public void A_result_with_an_unexpected_shape_gets_no_card_rather_than_a_wrong_one()
    {
        var card = ToolCards.From(Completed("check_warranty", "{\"equipmentId\":\"EQ-501\"}", "{\"equipmentId\":\"EQ-501\",\"covered\":\"maybe\"}"));

        Assert.Equal(ToolCardState.Succeeded, card.State);
        Assert.Null(card.Body);
    }

    [Fact]
    public void A_long_parts_list_is_counted_but_only_partly_rendered()
    {
        var parts = string.Join(',', Enumerable.Range(1, 30).Select(i =>
            $"{{\"sku\":\"P-{i}\",\"name\":\"Part {i}\",\"unitPrice\":{i}.5,\"inStock\":{i % 3},\"warehouse\":\"Dover\"}}"));

        var body = Assert.IsType<PartsBody>(ToolCards.From(Completed("search_parts", "{\"query\":\"\"}", $"[{parts}]")).Body);

        Assert.Equal(30, body.TotalCount);
        Assert.Equal(ToolCards.MaxPartRows, body.Parts.Count);
    }

    [Fact]
    public void An_unchanged_call_reuses_its_card()
    {
        var call = Completed("check_warranty", "{\"equipmentId\":\"EQ-501\"}",
            "{\"equipmentId\":\"EQ-501\",\"covered\":true,\"expiresOn\":\"2029-05-14\",\"coverage\":\"Parts: 10 years\"}");

        Assert.Same(ToolCards.From(call), ToolCards.From(call));
        Assert.Equal(new DateOnly(2029, 5, 14), Assert.IsType<WarrantyBody>(ToolCards.From(call).Body).ExpiresOn);
    }

    private const string WorkOrderResult =
        """
        {"workOrder":{"id":"WO-1001","customerId":"CUST-100","equipmentId":"EQ-501","summary":"Air conditioner short-cycling and blowing warm air",
         "status":"Scheduled","priority":"High","assignedTechnicianId":"TECH-1","scheduledFor":"2026-11-16T09:00:00-05:00",
         "checklist":[{"id":"C1","text":"a","done":false},{"id":"C2","text":"b","done":false},{"id":"C3","text":"c","done":true},{"id":"C4","text":"d","done":false}],"notes":[]},
         "customer":{"id":"CUST-100","name":"Harbor View Dental"},
         "equipment":{"id":"EQ-501","manufacturer":"Contoso Air","model":"CA-36X"}}
        """;

    private static (ToolCardState, string) Headline(ToolCallView call)
    {
        var card = ToolCards.From(call);
        return (card.State, card.Headline);
    }

    private static ToolCallView Completed(string name, string arguments, string result) =>
        new("call-" + Guid.NewGuid().ToString("N"), name)
        {
            Status = ToolCallStatus.Completed,
            ArgumentsText = arguments,
            Arguments = Json(arguments),
            Result = result,
        };

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
