using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ch27.Beacon.Desk.Identity;

/// <summary>Gets an access token for Beacon's agent on behalf of the signed-in user.</summary>
public interface IAgentTokenSource
{
    ValueTask<string> GetTokenAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}

/// <summary>The scopes the Desk asks for on the user's behalf, bound from <c>BeaconAgent:Scopes</c>.</summary>
public sealed class AgentAccessOptions
{
    public const string SectionName = "BeaconAgent";

    /// <summary>The agent's delegated scope in Microsoft Entra ID, for example <c>api://beacon-agent/access_as_user</c>.</summary>
    public string[] Scopes { get; set; } = [];
}

/// <summary>
/// Production: Microsoft Entra ID, through Microsoft.Identity.Web's token cache. The token's audience is the
/// agent; the user's Entra app roles (<c>dispatcher</c>, <c>technician</c>) arrive as role claims, which is
/// what Chapter 23's policy checks. Built, not run: running it needs an Entra tenant.
/// </summary>
public sealed class EntraAgentTokenSource(ITokenAcquisition tokens, IOptions<AgentAccessOptions> options) : IAgentTokenSource
{
    public async ValueTask<string> GetTokenAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        try
        {
            return await tokens.GetAccessTokenForUserAsync(
                options.Value.Scopes,
                user: user,
                tokenAcquisitionOptions: new TokenAcquisitionOptions { CancellationToken = cancellationToken });
        }
        catch (Exception ex) when (ex is MicrosoftIdentityWebChallengeUserException or MsalUiRequiredException)
        {
            // The token cache is in this process's memory and the sign-in cookie isn't: after a restart, a new
            // revision or a move to another replica, the cookie is valid and the cache has no account for it.
            throw new SignInRequiredException(ex);
        }
    }
}

/// <summary>
/// The dispatcher is signed in, but the Desk holds no token for the agent on their behalf, so only signing in
/// again can get one. <c>DeskChat</c> sends them through <see cref="DeskAuthentication.SignInAgainPath"/>.
/// </summary>
public sealed class SignInRequiredException(Exception inner)
    : Exception("The signed-in user has no token for Beacon's agent; they must sign in again.", inner);

/// <summary>The key, issuer and audience the agent's development settings trust, bound from <c>BeaconAgent:DevelopmentTokens</c>.</summary>
public sealed class DevelopmentTokenOptions
{
    public const string SectionName = "BeaconAgent:DevelopmentTokens";

    public string Issuer { get; set; } = "beacon-dev";

    public string Audience { get; set; } = "beacon-agent";

    /// <summary>A base64 HMAC key. The AppHost makes a fresh one for every local run and gives it to both apps.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Development only: signs a short-lived token for the signed-in user with the key the agent's development
/// settings trust, the way <c>dotnet user-jwts</c> does for Chapter 23. The Desk never registers it outside
/// Development, and a deployed agent trusts only Microsoft Entra ID.
/// </summary>
public sealed class DevelopmentAgentTokenSource(IOptions<DevelopmentTokenOptions> options, TimeProvider time) : IAgentTokenSource
{
    private static readonly JsonWebTokenHandler Handler = new();

    public ValueTask<string> GetTokenAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("The signed-in user has no subject."),
            ["unique_name"] = user.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            ["role"] = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
        };
        if (user.FindFirstValue("tech_id") is { Length: > 0 } technician)
        {
            claims["tech_id"] = technician;
        }

        var now = time.GetUtcNow().UtcDateTime;
        return ValueTask.FromResult(Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + settings.Lifetime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(settings.SigningKey)), SecurityAlgorithms.HmacSha256),
        }));
    }
}

/// <summary>The people a developer can sign in as on <c>/dev/sign-in</c>.</summary>
public static class DevelopmentUsers
{
    public static ClaimsPrincipal? Find(string? who) => who switch
    {
        "dana" => Principal("dana", "Dana Kim", "dispatcher"),
        "priya" => Principal("priya", "Priya Raman", "technician", "TECH-1"),
        _ => null,
    };

    private static ClaimsPrincipal Principal(string subject, string name, string role, string? technicianId = null)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, subject), new(ClaimTypes.Name, name), new(ClaimTypes.Role, role)];
        if (technicianId is not null)
        {
            claims.Add(new Claim("tech_id", technicianId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Development", ClaimTypes.Name, ClaimTypes.Role));
    }
}
