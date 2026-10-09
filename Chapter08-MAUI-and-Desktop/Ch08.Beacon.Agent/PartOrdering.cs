using System.ComponentModel;
using AGUI.Server;
using Beacon.AI;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch08.Beacon.Agent;

/// <summary>
/// The one consequential tool in Chapter 8: ordering a part, so the clients have a side effect that
/// must not happen twice. Orders over Contoso Climate's $500 limit are refused in code here; Chapter 14
/// replaces the refusal with a dispatcher's approval.
/// </summary>
public sealed partial class PartOrdering(IFieldServiceStore store, ILogger<PartOrdering> logger)
{
    private readonly BeaconTools _tools = new(store);

    public AIFunction CreateTool() => AIFunctionFactory.Create(
        PlaceOrder,
        BeaconTools.PlacePartOrderName,
        "Orders replacement parts for a work order. Orders over $500 need a dispatcher's approval and are refused.",
        BeaconJson.Options);

    public object PlaceOrder(
        [Description("The work order the parts are for.")] string workOrderId,
        [Description("The part SKU, for example CAP-45-5.")] string sku,
        [Description("How many to order, from 1 to 10.")] int quantity)
    {
        if (store.GetPart(sku) is { } part && SpendingPolicy.RequiresApproval(part.UnitPrice * quantity))
        {
            return new ToolError(
                $"{quantity} x {sku} costs ${part.UnitPrice * quantity:0.00}, over the ${SpendingPolicy.ApprovalThreshold:0} limit. " +
                "A dispatcher has to approve it from Beacon Desk.");
        }

        var result = _tools.PlacePartOrder(workOrderId, sku, quantity);
        if (result is PartOrder order)
        {
            // The side effect this chapter counts: one log line per order, with the run that placed it.
            var runId = FunctionInvokingChatClient.CurrentContext?.Options is { } options && options.TryGetRunAgentInput(out var input)
                ? input.RunId
                : "(no run)";
            LogPlaced(logger, order.Id, order.Quantity, order.Sku, order.WorkOrderId, runId);
        }

        return result;
    }

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information,
        Message = "Placed {OrderId}: {Quantity} x {Sku} for {WorkOrderId} in run {RunId}.")]
    private static partial void LogPlaced(ILogger logger, string orderId, int quantity, string sku, string workOrderId, string runId);
}
