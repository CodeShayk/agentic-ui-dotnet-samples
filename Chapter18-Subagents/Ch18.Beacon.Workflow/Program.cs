using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch18.Beacon.Agent.Specialists;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Ch18.Beacon.Workflow;

// Program lives in a namespace (not top-level statements) so Ch18.Tests can host it beside the agent.
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Agent Framework's AG-UI hosting, with Chapter 5's fix: the AG-UI resolver first, so unset fields stay unset.
        builder.Services.AddAGUIServer();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver));

        var lookups = new BeaconTools(new InMemoryFieldServiceStore()).CreateReadOnlyTools();
        var model = AIClientFactory.CreateChatClient(builder.Configuration, [.. SpecialistScripts.Rules, .. BeaconScripts.Default]);

        AIAgent Specialist(string name, string instructions, params string[] tools) =>
            model.AsAIAgent(instructions: instructions, name: name, tools: [.. lookups.Where(t => tools.Contains(t.Name))]);

        // The same three specialists, each a ChatClientAgent, fanned out by a concurrent workflow that
        // sends every one of them the dispatcher's message. The workflow itself is hosted as one agent.
        var team = AgentWorkflowBuilder.BuildConcurrent("beacon-team",
            [
                Specialist("parts-specialist", "Find the part that fits with search_parts and report SKU, price and stock.", BeaconTools.SearchPartsName),
                Specialist("scheduling-specialist", "Find the earliest free slot today with get_schedule.", BeaconTools.GetScheduleName),
                Specialist("warranty-specialist", "Check the warranty with check_warranty.", BeaconTools.CheckWarrantyName),
            ])
            .AsAIAgent(name: "beacon-team");

        var app = builder.Build();

        app.MapAGUIServer("/agent", team);

        app.Run();
    }
}
