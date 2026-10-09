using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace Ch27.SmokeTest;

/// <summary>The token the smoke run is sent with.</summary>
public static class SmokeToken
{
    /// <summary>
    /// Under <c>aspire run</c>, a short-lived token signed with the AppHost's development key, as the Desk signs
    /// its users' tokens. In Azure, a token from Microsoft Entra ID for the job's own user-assigned identity
    /// (<c>smoke-identity</c>, named by <c>AZURE_CLIENT_ID</c>), which needs the agent's <c>dispatcher</c> app role
    /// (assigned once, in Entra). With neither, no token: the run check fails.
    /// </summary>
    public static async Task<string?> GetAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration["BeaconAgent:DevelopmentTokens:SigningKey"] is { Length: > 0 } key)
        {
            return CreateDevelopmentToken(Convert.FromBase64String(key), DateTimeOffset.UtcNow);
        }

        if (configuration["Smoke:TokenScope"] is { Length: > 0 } scope)
        {
            // Only the job's own identity: never a developer's sign-in, and never whichever identity a host offers first.
            var clientId = configuration["AZURE_CLIENT_ID"] is { Length: > 0 } id
                ? id
                : throw new InvalidOperationException("Smoke:TokenScope is set but AZURE_CLIENT_ID isn't: the job has no identity of its own.");
            var credential = new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
            var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
            return token.Token;
        }

        return null;
    }

    /// <summary>An HS256 token for "smoke", a dispatcher, issued and audienced as the AppHost's development settings expect.</summary>
    public static string CreateDevelopmentToken(byte[] key, DateTimeOffset now)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = "HS256", ["typ"] = "JWT" }));
        var claims = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = "beacon-dev",
            ["aud"] = "beacon-agent",
            ["sub"] = "smoke",
            ["unique_name"] = "Smoke test",
            ["role"] = "dispatcher",
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(5).ToUnixTimeSeconds(),
        }));
        var signature = Encode(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{header}.{claims}")));
        return $"{header}.{claims}.{signature}";
    }
}
