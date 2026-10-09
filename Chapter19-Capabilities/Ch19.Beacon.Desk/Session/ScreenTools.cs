using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch19.Beacon.Desk.Session;

/// <summary>The Desk's frontend tools: things the agent may do on the dispatcher's screen (Chapter 10).</summary>
public static class ScreenTools
{
    public const string OpenWorkOrderName = "open_work_order";

    /// <summary>Opens a work order in the Desk's work area. The agent's argument is checked like any other input.</summary>
    public static AIFunction OpenWorkOrder(DeskSession session) => AIFunctionFactory.Create(
        (string workOrderId) =>
        {
            if (SeedData.WorkOrders.FirstOrDefault(w => string.Equals(w.Id, workOrderId, StringComparison.OrdinalIgnoreCase)) is not { } workOrder)
            {
                return $"There is no work order {workOrderId}.";
            }

            session.ShowWorkOrder(workOrder.Id);
            return $"Opened {workOrder.Id} on the dispatcher's screen.";
        },
        OpenWorkOrderName,
        "Opens a work order on the dispatcher's screen.");
}
