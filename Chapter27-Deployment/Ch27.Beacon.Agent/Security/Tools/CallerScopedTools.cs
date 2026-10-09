// Copied from Chapter 23 (Ch23.Beacon.Security).
using System.ComponentModel;
using System.Text.Json;
using Beacon.AI;
using Beacon.Domain;
using Ch27.Beacon.Agent.Security.Identity;
using Microsoft.Extensions.AI;

namespace Ch27.Beacon.Agent.Security.Tools;

/// <summary>
/// Beacon's tools with the caller built in. Identity comes from <paramref name="caller"/>, which the
/// host fills from the authenticated principal; no tool takes a user, role or technician as an
/// argument it trusts, so nothing the model writes can widen what a tool may see or do.
/// </summary>
public sealed class CallerScopedTools(IFieldServiceStore store, Func<BeaconCaller> caller)
{
    /// <summary>
    /// Compact JSON for results. <c>BeaconJson.Options</c> inherits indentation, and every result is stored in the
    /// transcript and echoed back by the client on each turn (Chapter 22).
    /// </summary>
    public static JsonSerializerOptions Json { get; } = new(BeaconJson.Options) { WriteIndented = false };

    /// <summary>The read-only tools, each scoped to what the caller may see.</summary>
    public IList<AITool> CreateReadOnlyTools() =>
    [
        Create(GetWorkOrder, BeaconTools.GetWorkOrderName, "Gets a work order with its customer and equipment."),
        Create(ListWorkOrders, BeaconTools.ListWorkOrdersName, "Lists the work orders the user may see, optionally filtered by status."),
        Create(SearchParts, BeaconTools.SearchPartsName, "Searches stocked parts by name or SKU, optionally for one equipment model."),
        Create(CheckWarranty, BeaconTools.CheckWarrantyName, "Gets the warranty status of a piece of equipment."),
        Create(GetFaultHistory, BeaconTools.GetFaultHistoryName, "Gets the recorded fault codes for a piece of equipment."),
        Create(GetSchedule, BeaconTools.GetScheduleName, "Gets today's schedule. Technicians see only their own."),
    ];

    /// <summary>
    /// Every tool the caller may use. The consequential ones go through <paramref name="guard"/>, the check
    /// at the tool, and only dispatchers get <c>charge_customer</c> at all.
    /// </summary>
    public IList<AITool> CreateTools(Func<AIFunction, AIFunction> guard)
    {
        var tools = CreateReadOnlyTools();
        tools.Add(guard(Create(PlacePartOrder, BeaconTools.PlacePartOrderName, "Orders replacement parts for a work order.")));

        if (caller().IsDispatcher)
        {
            tools.Add(guard(Create(ChargeCustomer, BeaconTools.ChargeCustomerName, "Charges the customer for work done on a work order.")));
        }

        return tools;
    }

    public object GetWorkOrder([Description("The work order ID, for example WO-1001.")] string workOrderId)
    {
        // "Not yours" and "doesn't exist" get the same answer, so the tool can't be used to probe.
        if (store.GetWorkOrder(workOrderId) is not { } workOrder || !CanSee(workOrder))
        {
            return new ToolError($"Work order {workOrderId} was not found.");
        }

        return new WorkOrderDetails(workOrder, store.GetCustomer(workOrder.CustomerId)!, store.GetEquipment(workOrder.EquipmentId)!);
    }

    public IReadOnlyList<WorkOrder> ListWorkOrders([Description("Optional status filter.")] WorkOrderStatus? status = null) =>
        store.ListWorkOrders(status, caller().TechnicianId);

    public IReadOnlyList<Part> SearchParts(
        [Description("Words to search for in the part name or SKU, for example 'capacitor'.")] string query,
        [Description("Optional equipment model the part must fit, for example CA-36X.")] string? model = null) =>
        store.SearchParts(query, model);

    public object CheckWarranty([Description("The equipment ID, for example EQ-501.")] string equipmentId) =>
        store.GetWarranty(equipmentId) is { } warranty ? warranty : new ToolError($"No warranty record for {equipmentId}.");

    public IReadOnlyList<FaultRecord> GetFaultHistory([Description("The equipment ID, for example EQ-501.")] string equipmentId) =>
        store.GetFaultHistory(equipmentId);

    public object GetSchedule([Description("Optional technician ID, for example TECH-1.")] string? technicianId = null)
    {
        var who = caller();

        // A filter, not an identity: dispatchers may pick any technician, technicians only themselves.
        if (!who.IsDispatcher && technicianId is not null && !string.Equals(technicianId, who.TechnicianId, StringComparison.OrdinalIgnoreCase))
        {
            return new ToolError("Technicians can see only their own schedule.");
        }

        return store.GetSchedule(SeedData.Today, who.IsDispatcher ? technicianId : who.TechnicianId);
    }

    public object PlacePartOrder(
        [Description("The work order the parts are for.")] string workOrderId,
        [Description("The part SKU, for example CAP-45-5.")] string sku,
        [Description("How many to order, from 1 to 10.")] int quantity)
    {
        var who = caller();
        if (store.GetWorkOrder(workOrderId) is not { } workOrder || !CanSee(workOrder))
        {
            return new ToolError($"Work order {workOrderId} was not found.");
        }

        // The price comes from the server's catalog, never from the model. (The check at the tool refuses
        // this first; the rule is repeated here because the tool is the last line.)
        if (!who.IsDispatcher && store.GetPart(sku) is { } part && SpendingPolicy.RequiresApproval(part.UnitPrice * quantity))
        {
            return new ToolError($"Orders over ${SpendingPolicy.ApprovalThreshold:0} need a dispatcher. Ask dispatch to place this one.");
        }

        try
        {
            return store.PlacePartOrder(workOrderId, sku, quantity);
        }
        catch (FieldServiceException ex)
        {
            return new ToolError(ex.Message);
        }
    }

    public object ChargeCustomer(
        [Description("The work order the charge is for.")] string workOrderId,
        [Description("The amount in US dollars.")] decimal amount,
        [Description("What the charge is for, as it should appear on the invoice.")] string description)
    {
        // Only dispatchers are offered this tool; the check repeats here because the tool is the last line.
        if (!caller().IsDispatcher)
        {
            return new ToolError("Only a dispatcher can charge a customer.");
        }

        try
        {
            return store.ChargeCustomer(workOrderId, amount, description);
        }
        catch (FieldServiceException ex)
        {
            return new ToolError(ex.Message);
        }
    }

    private bool CanSee(WorkOrder workOrder) =>
        caller() is var who && (who.IsDispatcher || string.Equals(workOrder.AssignedTechnicianId, who.TechnicianId, StringComparison.OrdinalIgnoreCase));

    private static AIFunction Create(Delegate method, string name, string description) =>
        AIFunctionFactory.Create(method, name, description, Json);
}
