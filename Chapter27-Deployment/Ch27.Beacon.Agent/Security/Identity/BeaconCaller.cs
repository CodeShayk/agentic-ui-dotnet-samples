// Copied from Chapter 23 (Ch23.Beacon.Security).
using System.Security.Claims;

namespace Ch27.Beacon.Agent.Security.Identity;

/// <summary>The two kinds of people who use Beacon.</summary>
public enum BeaconRole
{
    Dispatcher,
    Technician,
}

/// <summary>Role names and claim types as they appear in Beacon's access tokens.</summary>
public static class BeaconClaims
{
    public const string DispatcherRole = "dispatcher";
    public const string TechnicianRole = "technician";

    /// <summary>The technician a technician's token belongs to, for example <c>TECH-1</c>.</summary>
    public const string TechnicianId = "tech_id";
}

/// <summary>
/// Who is calling, as established by authentication. Everything security-relevant reads it from
/// here: the transcript key, the approval binding and the tools. Nothing in <c>RunAgentInput</c>,
/// and nothing a model writes in tool arguments, can change it.
/// </summary>
public sealed record BeaconCaller(string UserId, string DisplayName, BeaconRole Role, string? TechnicianId)
{
    public bool IsDispatcher => Role == BeaconRole.Dispatcher;

    /// <summary>
    /// Builds the caller from an authenticated principal, or returns <see langword="null"/> when the
    /// principal isn't a complete Beacon identity (no stable subject, no Beacon role, or a technician
    /// without a technician ID).
    /// </summary>
    public static BeaconCaller? FromPrincipal(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        // The subject ("sub", mapped to NameIdentifier) is stable and unique. A display name is neither.
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var displayName = user.FindFirstValue(ClaimTypes.Name) ?? userId;

        if (user.IsInRole(BeaconClaims.DispatcherRole))
        {
            return new BeaconCaller(userId, displayName, BeaconRole.Dispatcher, TechnicianId: null);
        }

        if (user.IsInRole(BeaconClaims.TechnicianRole)
            && user.FindFirstValue(BeaconClaims.TechnicianId) is { Length: > 0 } technicianId)
        {
            return new BeaconCaller(userId, displayName, BeaconRole.Technician, technicianId);
        }

        return null;
    }
}
