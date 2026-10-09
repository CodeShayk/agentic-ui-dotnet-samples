using System.Security.Cryptography;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Ch21.Beacon.Agent.Interop;

/// <summary>
/// This checkpoint's capability document (Chapter 19). A tool is the agent's if the agent executes it, so the
/// tools Beacon reaches in other systems are listed beside its own, in Beacon's pinned words: never the manual
/// library's own descriptions, never its write tool. Northwind's agent is a subagent Beacon delegates to. The
/// document is static because the remote contracts are pinned in code; a tool that goes unexercised while its
/// server is down is something the specification tells clients to tolerate.
/// </summary>
public static class InteropCapabilities
{
    public static AgentCapabilities Describe(IEnumerable<AIFunction> beaconTools, AIFunction supplierTool) => new()
    {
        Identity = new IdentityCapabilities { Name = "Beacon", Type = "ag-ui-dotnet", Provider = "Contoso Climate" },
        Transport = new TransportCapabilities { Streaming = true },
        Tools = new ToolsCapabilities
        {
            Supported = true,
            Items =
            [
                .. beaconTools.Select(Declare),
                .. ManualsToolSource.AllowedTools.Select(t => new AGUITool { Name = t.Key, Description = t.Value.Description, Parameters = t.Value.JsonSchema }),
                Declare(supplierTool),
            ],
            ClientProvided = true,
        },
        MultiAgent = new MultiAgentCapabilities { Supported = true, Delegation = true, Subagents = [InteropExtensions.Supplier] },
    };

    private static AGUITool Declare(AIFunction tool) => new() { Name = tool.Name, Description = tool.Description, Parameters = tool.JsonSchema };

    /// <summary>
    /// Serves the document at <paramref name="pattern"/> (GET only), as Chapter 19's agent does. AG-UI 1.0 defines
    /// the document's shape but no way to fetch it, so the address is Beacon's convention.
    /// </summary>
    public static IEndpointConventionBuilder MapAgentCapabilities(
        this IEndpointRouteBuilder endpoints, string pattern, AgentCapabilities capabilities)
    {
        // The SDK's source-generated context writes the 1.0 wire shape: camelCase, unset fields omitted.
        var body = JsonSerializer.SerializeToUtf8Bytes(capabilities, AGUIJsonSerializerContext.Default.AgentCapabilities);
        var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(body))[..16]}\"";

        return endpoints.MapGet(pattern, (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.ETag = etag;
            return context.Request.Headers.IfNoneMatch.Contains(etag)
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Bytes(body, "application/json");
        });
    }
}
