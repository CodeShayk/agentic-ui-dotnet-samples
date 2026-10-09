using System.Security.Cryptography;
using System.Text.Json;
using AGUI.Abstractions;

namespace Ch19.Beacon.Agent.Capabilities;

public static class CapabilityEndpoint
{
    /// <summary>
    /// Serves the capabilities document at <paramref name="pattern"/> (GET only). AG-UI 1.0 defines the
    /// document's shape but no way to fetch it, so this address is Beacon's own convention.
    /// </summary>
    public static IEndpointConventionBuilder MapAgentCapabilities(
        this IEndpointRouteBuilder endpoints, string pattern, AgentCapabilities capabilities)
    {
        // The SDK's source-generated context writes the 1.0 wire shape: camelCase, unset fields omitted.
        var body = JsonSerializer.SerializeToUtf8Bytes(capabilities, AGUIJsonSerializerContext.Default.AgentCapabilities);
        var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(body))[..16]}\"";

        return endpoints.MapGet(pattern, (HttpContext context) =>
        {
            // Clients may keep a copy, but must check it again: a redeployment can change the profile.
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.ETag = etag;
            return context.Request.Headers.IfNoneMatch.Contains(etag)
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Bytes(body, "application/json");
        });
    }
}
