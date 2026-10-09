using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ch23.Tests.Infrastructure;

/// <summary>
/// Access tokens for the tests, signed with a key only the tests know. The hosts read the issuer,
/// audience and key from the same configuration keys <c>dotnet user-jwts</c> uses.
/// </summary>
public static class TestTokens
{
    public const string Issuer = "beacon-tests";
    public const string Audience = "beacon-agent";

    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    public static IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
        ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
        ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
        ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(Key),
    };

    /// <summary>Dana Kim, dispatcher.</summary>
    public static string Dana { get; } = Create("dana", "Dana Kim", "dispatcher");

    /// <summary>Priya Raman, technician TECH-1 (WO-1001 is hers).</summary>
    public static string Priya { get; } = Create("priya", "Priya Raman", "technician", technicianId: "TECH-1");

    /// <summary>A valid token for someone who isn't Contoso staff.</summary>
    public static string Customer { get; } = Create("guest-7", "Harbor View front desk", "customer");

    public static string Create(
        string subject,
        string name,
        string role,
        string? technicianId = null,
        byte[]? signingKey = null,
        DateTime? expires = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject, ["unique_name"] = name, ["role"] = role };
        if (technicianId is not null)
        {
            claims["tech_id"] = technicianId;
        }

        var expiry = expires ?? DateTime.UtcNow.AddHours(1);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = claims,
            IssuedAt = expiry.AddHours(-2),
            NotBefore = expiry.AddHours(-2),
            Expires = expiry,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(signingKey ?? Key), SecurityAlgorithms.HmacSha256),
        });
    }
}
