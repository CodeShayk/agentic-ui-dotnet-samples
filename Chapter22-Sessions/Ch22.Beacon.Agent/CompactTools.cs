using System.Text.Json;
using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch22.Beacon.Agent;

/// <summary>
/// Beacon's read-only tools (Chapter 9), returning compact JSON. <c>BeaconJson.Options</c> inherits indentation
/// from Microsoft.Extensions.AI's defaults, and once a server keeps conversations every byte of a result is
/// stored, restated in <c>MESSAGES_SNAPSHOT</c> and counted against the model's budget.
/// </summary>
public static class CompactTools
{
    private static readonly JsonSerializerOptions Options = new(BeaconJson.Options) { WriteIndented = false };

    public static IList<AITool> Create(BeaconTools tools) =>
    [
        AIFunctionFactory.Create(tools.GetWorkOrder, BeaconTools.GetWorkOrderName, "Gets a work order with its customer and equipment.", Options),
        AIFunctionFactory.Create(tools.ListWorkOrders, BeaconTools.ListWorkOrdersName, "Lists work orders, optionally filtered by status.", Options),
        AIFunctionFactory.Create(tools.SearchParts, BeaconTools.SearchPartsName, "Searches stocked parts by name or SKU, optionally for one equipment model.", Options),
        AIFunctionFactory.Create(tools.CheckWarranty, BeaconTools.CheckWarrantyName, "Gets the warranty status of a piece of equipment.", Options),
        AIFunctionFactory.Create(tools.GetFaultHistory, BeaconTools.GetFaultHistoryName, "Gets the recorded fault codes for a piece of equipment.", Options),
        AIFunctionFactory.Create(tools.GetSchedule, BeaconTools.GetScheduleName, "Gets today's schedule, optionally for one technician.", Options),
    ];
}
