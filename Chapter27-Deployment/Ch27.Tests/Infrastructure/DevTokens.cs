using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ch27.Tests;

/// <summary>
/// Development tokens as the AppHost arranges them under <c>aspire run</c>: one key, issuer <c>beacon-dev</c>,
/// audience <c>beacon-agent</c>. The agent reads them from <c>Authentication:Schemes:Bearer</c>, the Desk and the
/// smoke test from <c>BeaconAgent:DevelopmentTokens</c>.
/// </summary>
public static class DevTokens
{
    public const string Issuer = "beacon-dev";
    public const string Audience = "beacon-agent";

    public static string Key { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static IReadOnlyDictionary<string, string?> AgentSettings { get; } = new Dictionary<string, string?>
    {
        ["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
        ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
        ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
        ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Key,
    };

    /// <summary>Dana Kim, dispatcher.</summary>
    public static string Dana { get; } = Create("dana", "Dana Kim", "dispatcher");

    /// <summary>Priya Raman, technician TECH-1.</summary>
    public static string Priya { get; } = Create("priya", "Priya Raman", "technician", "TECH-1");

    public static string Create(string subject, string name, string role, string? technicianId = null, byte[]? signingKey = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject, ["unique_name"] = name, ["role"] = role };
        if (technicianId is not null)
        {
            claims["tech_id"] = technicianId;
        }

        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = claims,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(signingKey ?? Convert.FromBase64String(Key)), SecurityAlgorithms.HmacSha256),
        });
    }
}
