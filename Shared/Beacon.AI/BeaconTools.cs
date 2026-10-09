using System.ComponentModel;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Beacon.AI;

/// <summary>A work order with the customer and equipment it concerns.</summary>
public sealed record WorkOrderDetails(WorkOrder WorkOrder, Customer Customer, Equipment Equipment);

/// <summary>What a tool returns when a request breaks a rule, so the model can explain it.</summary>
public sealed record ToolError(string Error);

/// <summary>
/// Beacon's backend tools. Each is narrow, typed and validated inside the tool; business rules
/// live in <see cref="IFieldServiceStore"/>, not in the prompt.
/// </summary>
public sealed class BeaconTools(IFieldServiceStore store)
{
    public const string GetWorkOrderName = "get_work_order";
    public const string ListWorkOrdersName = "list_work_orders";
    public const string SearchPartsName = "search_parts";
    public const string CheckWarrantyName = "check_warranty";
    public const string GetFaultHistoryName = "get_fault_history";
    public const string GetScheduleName = "get_schedule";
    public const string PlacePartOrderName = "place_part_order";
    public const string ChargeCustomerName = "charge_customer";

    /// <summary>Tools that only read data.</summary>
    public IList<AITool> CreateReadOnlyTools() =>
    [
        AIFunctionFactory.Create(GetWorkOrder, GetWorkOrderName, "Gets a work order with its customer and equipment.", BeaconJson.Options),
        AIFunctionFactory.Create(ListWorkOrders, ListWorkOrdersName, "Lists work orders, optionally filtered by status.", BeaconJson.Options),
        AIFunctionFactory.Create(SearchParts, SearchPartsName, "Searches stocked parts by name or SKU, optionally for one equipment model.", BeaconJson.Options),
        AIFunctionFactory.Create(CheckWarranty, CheckWarrantyName, "Gets the warranty status of a piece of equipment.", BeaconJson.Options),
        AIFunctionFactory.Create(GetFaultHistory, GetFaultHistoryName, "Gets the recorded fault codes for a piece of equipment.", BeaconJson.Options),
        AIFunctionFactory.Create(GetSchedule, GetScheduleName, "Gets today's schedule, optionally for one technician.", BeaconJson.Options),
    ];

    /// <summary>
    /// Tools with side effects. They are returned unwrapped; each chapter decides how to gate them
    /// (for example with <see cref="ApprovalRequiredAIFunction"/> in Chapter 14).
    /// </summary>
    public IList<AIFunction> CreateConsequentialTools() =>
    [
        AIFunctionFactory.Create(PlacePartOrder, PlacePartOrderName, "Orders replacement parts for a work order.", BeaconJson.Options),
        AIFunctionFactory.Create(ChargeCustomer, ChargeCustomerName, "Charges the customer for work done on a work order.", BeaconJson.Options),
    ];

    public object GetWorkOrder(
        [Description("The work order ID, for example WO-1001.")] string workOrderId)
    {
        var workOrder = store.GetWorkOrder(workOrderId);
        if (workOrder is null)
        {
            return new ToolError($"Work order {workOrderId} was not found.");
        }

        return new WorkOrderDetails(
            workOrder,
            store.GetCustomer(workOrder.CustomerId)!,
            store.GetEquipment(workOrder.EquipmentId)!);
    }

    public IReadOnlyList<WorkOrder> ListWorkOrders(
        [Description("Optional status filter.")] WorkOrderStatus? status = null) =>
        store.ListWorkOrders(status);

    public IReadOnlyList<Part> SearchParts(
        [Description("Words to search for in the part name or SKU, for example 'capacitor'.")] string query,
        [Description("Optional equipment model the part must fit, for example CA-36X.")] string? model = null) =>
        store.SearchParts(query, model);

    public object CheckWarranty(
        [Description("The equipment ID, for example EQ-501.")] string equipmentId) =>
        store.GetWarranty(equipmentId) is { } warranty
            ? warranty
            : new ToolError($"No warranty record for {equipmentId}.");

    public IReadOnlyList<FaultRecord> GetFaultHistory(
        [Description("The equipment ID, for example EQ-501.")] string equipmentId) =>
        store.GetFaultHistory(equipmentId);

    public IReadOnlyList<ScheduleSlot> GetSchedule(
        [Description("Optional technician ID, for example TECH-1.")] string? technicianId = null) =>
        store.GetSchedule(SeedData.Today, technicianId);

    public object PlacePartOrder(
        [Description("The work order the parts are for.")] string workOrderId,
        [Description("The part SKU, for example CAP-45-5.")] string sku,
        [Description("How many to order, from 1 to 10.")] int quantity)
    {
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
        try
        {
            return store.ChargeCustomer(workOrderId, amount, description);
        }
        catch (FieldServiceException ex)
        {
            return new ToolError(ex.Message);
        }
    }
}
