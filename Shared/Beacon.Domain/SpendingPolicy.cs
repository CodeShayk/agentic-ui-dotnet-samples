namespace Beacon.Domain;

/// <summary>
/// Contoso Climate's spending rules. Orders and charges above the threshold need a dispatcher's approval.
/// The rule is enforced in code; the model is told about it but is never trusted to apply it.
/// </summary>
public static class SpendingPolicy
{
    public const decimal ApprovalThreshold = 500m;

    public static bool RequiresApproval(decimal amount) => amount > ApprovalThreshold;
}
