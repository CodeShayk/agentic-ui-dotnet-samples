using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Ch19.Beacon.Adaptive.Capabilities;
using Microsoft.Extensions.AI;

namespace Ch19.Beacon.Agent.Capabilities;

/// <summary>Writes the deployment's <see cref="AgentCapabilities"/> declaration.</summary>
public static class BeaconCapabilities
{
    public static AgentCapabilities Describe(BeaconDeployment deployment, IEnumerable<AIFunction> serverTools)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        return new AgentCapabilities
        {
            Identity = new IdentityCapabilities
            {
                Name = "Beacon",
                Type = "ag-ui-dotnet",
                Description = "Contoso Climate's field-service assistant: work orders, parts, warranty, fault history and schedules.",
                Version = BeaconDeployment.AgentVersion,
                Provider = "Contoso Climate",
                Metadata = Json(new JsonObject { ["profile"] = deployment.Profile.ToString().ToLowerInvariant() }),
            },
            Transport = new TransportCapabilities { Streaming = true, HttpBinary = true },
            Tools = new ToolsCapabilities
            {
                Supported = true,
                // The agent's own tools, as its model sees them. Never the client's: those arrive per run.
                Items = [.. serverTools.Select(tool => new AGUITool { Name = tool.Name, Description = tool.Description, Parameters = tool.JsonSchema })],
                ClientProvided = deployment.AcceptsClientTools,
            },
            Reasoning = deployment.Reasons
                ? new ReasoningCapabilities { Supported = true, Streaming = true, Encrypted = false }
                : new ReasoningCapabilities { Supported = false },
            Multimodal = new MultimodalCapabilities
            {
                Input = new MultimodalInputCapabilities
                {
                    Image = deployment.AcceptsImages,
                    Audio = deployment.AcceptsAudio,
                    Pdf = deployment.AcceptsPdf,
                    Video = false,
                    File = false,
                },
            },
            Execution = new ExecutionCapabilities { CodeExecution = false, MaxIterations = deployment.MaxToolIterations },
            HumanInTheLoop = new HumanInTheLoopCapabilities { Supported = false },

            // Nothing in the standard groups says how big an attachment may be, so Beacon says it here.
            Custom = Json(new JsonObject
            {
                [AgentProfile.AttachmentLimitsKey] = new JsonObject(
                    deployment.AttachmentLimits.Select(l => KeyValuePair.Create(l.Key, (JsonNode?)l.Value))),
            }),

            // state, output and multiAgent are left out: this checkpoint has nothing to say about them,
            // and a client must read their absence as "unknown", not as "no".
        };
    }

    private static JsonElement Json(JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }
}
