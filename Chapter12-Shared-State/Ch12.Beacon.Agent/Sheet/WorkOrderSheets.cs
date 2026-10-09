using Beacon.Domain;

namespace Ch12.Beacon.Agent.Sheet;

/// <summary>How the sheet relates to the work-order system, which stays the source of truth for what the server owns.</summary>
internal static class WorkOrderSheets
{
    /// <summary>
    /// A work order as the sheet shows it: an explicit projection, never a serialized domain record, so
    /// the customer's phone and address, the site's coordinates and the unit's serial number stay on the server.
    /// </summary>
    public static WorkOrderSheet? Project(IFieldServiceStore store, string workOrderId)
    {
        if (store.GetWorkOrder(workOrderId) is not { } workOrder)
        {
            return null;
        }

        var owned = Owned(store, workOrder);
        return new WorkOrderSheet
        {
            Id = owned.Id,
            Customer = owned.Customer,
            Equipment = owned.Equipment,
            Summary = owned.Summary,
            Priority = owned.Priority,
            Technician = owned.Technician,
            Status = workOrder.Status,
            Checklist = workOrder.Checklist,
            Notes = workOrder.Notes,
        };
    }

    /// <summary>
    /// Reconciles the sheet a client sent with the work-order system: an older shape is upgraded and the
    /// fields the server owns are copied from the store. The shared fields are the client's to keep.
    /// </summary>
    /// <param name="sent">The state from <c>RunAgentInput.state</c>, already validated against the schema.</param>
    /// <param name="store">The work-order system.</param>
    /// <param name="repaired">True when the result differs from what the client sent.</param>
    public static SheetState Reconcile(SheetState sent, IFieldServiceStore store, out bool repaired)
    {
        var reconciled = sent with { SchemaVersion = SheetState.CurrentVersion };

        if (sent.WorkOrder is { } sheet && store.GetWorkOrder(sheet.Id) is { } workOrder)
        {
            var owned = Owned(store, workOrder);
            reconciled = reconciled with
            {
                WorkOrder = sheet with
                {
                    Customer = owned.Customer,
                    Equipment = owned.Equipment,
                    Summary = owned.Summary,
                    Priority = owned.Priority,
                    Technician = owned.Technician,
                },
            };
        }

        // `with` keeps the checklist and notes lists, so record equality compares only what changed.
        repaired = reconciled != sent;
        return reconciled;
    }

    private static WorkOrderSheet Owned(IFieldServiceStore store, WorkOrder workOrder)
    {
        var customer = store.GetCustomer(workOrder.CustomerId);
        var equipment = store.GetEquipment(workOrder.EquipmentId);
        var technician = store.ListTechnicians().FirstOrDefault(t => t.Id == workOrder.AssignedTechnicianId);

        return new WorkOrderSheet
        {
            Id = workOrder.Id,
            Customer = customer?.Name ?? workOrder.CustomerId,
            Equipment = equipment is null ? workOrder.EquipmentId : $"{equipment.Manufacturer} {equipment.Model}",
            Summary = workOrder.Summary,
            Priority = workOrder.Priority,
            Technician = technician?.Name ?? "Unassigned",
            Status = workOrder.Status,
            Checklist = [],
        };
    }
}
