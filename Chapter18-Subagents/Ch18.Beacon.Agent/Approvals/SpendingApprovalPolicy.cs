// Copied from Chapter 14 (Ch14.Beacon.Agent/Approvals); each chapter's code is a self-contained checkpoint.
using System.Globalization;
using System.Text.Json;
using Beacon.AI;
using Beacon.Domain;

namespace Ch18.Beacon.Agent.Approvals;

/// <summary>What the spending policy says about one tool call.</summary>
public enum ApprovalNeed
{
    /// <summary>The tool doesn't spend money; the policy has nothing to say.</summary>
    NotGated,

    /// <summary>A valid call at or under the threshold. It runs without asking anyone.</summary>
    WithinLimit,

    /// <summary>A valid call over the threshold. A dispatcher must approve it first.</summary>
    Required,

    /// <summary>Not a call the tool would accept: an unknown part, a quantity out of range and so on.</summary>
    Invalid,
}

/// <param name="Need">Whether the call needs a dispatcher.</param>
/// <param name="Amount">The call's cost in US dollars, as the server prices it.</param>
/// <param name="Summary">One sentence a dispatcher can read, or what's wrong with the call.</param>
public sealed record SpendingAssessment(ApprovalNeed Need, decimal Amount, string Summary)
{
    public static SpendingAssessment NotGated { get; } = new(ApprovalNeed.NotGated, 0m, string.Empty);
}

/// <summary>
/// Contoso Climate's spending rule applied to a tool call. It prices the call from the server's own
/// catalog and work orders, never from a number the model or a client wrote, and the same check runs
/// three times: when the model proposes a call, when a dispatcher edits one and at the tool itself.
/// </summary>
public sealed class SpendingApprovalPolicy(IFieldServiceStore store)
{
    /// <summary>The arguments a dispatcher may change when approving, per gated tool. Everything else is fixed.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> EditableArguments { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [BeaconTools.PlacePartOrderName] = ["quantity"],
            [BeaconTools.ChargeCustomerName] = ["amount", "description"],
        };

    public SpendingAssessment Assess(string toolName, JsonElement arguments) => toolName switch
    {
        BeaconTools.PlacePartOrderName => AssessOrder(arguments),
        BeaconTools.ChargeCustomerName => AssessCharge(arguments),
        _ => SpendingAssessment.NotGated,
    };

    private SpendingAssessment AssessOrder(JsonElement arguments)
    {
        if (ToolArguments.String(arguments, "workOrderId") is not { } workOrderId || store.GetWorkOrder(workOrderId) is null)
        {
            return Invalid("The order must name an existing work order.");
        }

        if (ToolArguments.String(arguments, "sku") is not { } sku || store.GetPart(sku) is not { } part)
        {
            return Invalid("The order must name a stocked part.");
        }

        if (ToolArguments.Integer(arguments, "quantity") is not { } quantity || quantity is < 1 or > 10)
        {
            return Invalid("The quantity must be a whole number from 1 to 10.");
        }

        var total = part.UnitPrice * quantity;
        return Decide(total, $"Order {quantity} x {part.Sku} ({part.Name}) for {workOrderId}: {Money(total)}.");
    }

    private SpendingAssessment AssessCharge(JsonElement arguments)
    {
        if (ToolArguments.String(arguments, "workOrderId") is not { } workOrderId || store.GetWorkOrder(workOrderId) is not { } workOrder)
        {
            return Invalid("The charge must name an existing work order.");
        }

        if (ToolArguments.Decimal(arguments, "amount") is not { } amount || amount is <= 0m or > 25_000m || decimal.Round(amount, 2) != amount)
        {
            return Invalid("The amount must be between $0.01 and $25,000.00, in whole cents.");
        }

        if (ToolArguments.String(arguments, "description") is not { Length: >= 1 and <= 200 } description)
        {
            return Invalid("The description must be 1 to 200 characters long.");
        }

        var customer = store.GetCustomer(workOrder.CustomerId)?.Name ?? workOrder.CustomerId;
        return Decide(amount, $"Charge {customer} {Money(amount)} for {workOrderId} ({description}).");
    }

    private static SpendingAssessment Decide(decimal amount, string summary) =>
        new(SpendingPolicy.RequiresApproval(amount) ? ApprovalNeed.Required : ApprovalNeed.WithinLimit, amount, summary);

    private static SpendingAssessment Invalid(string problem) => new(ApprovalNeed.Invalid, 0m, problem);

    public static string Money(decimal amount) => amount.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
}
