using A2A;
using A2A.AspNetCore;

namespace Ch21.SupplierAgent;

// Program lives in a namespace so the chapter's test project can host all three servers side by side.
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var publicUrl = builder.Configuration["Supplier:PublicUrl"] ?? "http://localhost:5213/a2a";

        // The agent card is how A2A clients discover Northwind's agent: who runs it, where it
        // listens, which binding it speaks and what it can do.
        var card = new AgentCard
        {
            Name = "Northwind Supply parts agent",
            Description = "Answers part availability and lead-time questions for Northwind Supply's HVAC catalog.",
            Version = "1.0.0",
            Provider = new AgentProvider { Organization = "Northwind Supply", Url = "https://northwind.example" },
            SupportedInterfaces =
            [
                new AgentInterface { Url = publicUrl, ProtocolBinding = ProtocolBindingNames.JsonRpc, ProtocolVersion = "1.0" },
            ],
            Capabilities = new AgentCapabilities { Streaming = false, PushNotifications = false },
            DefaultInputModes = ["application/json", "text/plain"],
            DefaultOutputModes = ["application/json", "text/plain"],
            Skills =
            [
                new AgentSkill
                {
                    Id = "part-availability",
                    Name = "Part availability",
                    Description = "Quantity on hand, ship-from warehouse and lead time for a SKU.",
                    Tags = ["parts", "inventory", "lead-time"],
                    Examples = ["{\"sku\":\"EXP-TANK-4\",\"quantity\":1}", "2 x CAP-45-5"],
                },
            ],
        };

        // The SDK's easy path: an IAgentHandler plus a task store, JSON-RPC processing and the card.
        builder.Services.AddA2AAgent<SupplierAgentHandler>(card);

        var app = builder.Build();

        // POST /a2a: the JSON-RPC binding the card advertises.
        app.MapA2A("/a2a");

        // GET /.well-known/agent-card.json: discovery.
        app.MapWellKnownAgentCard(card);

        app.Run();
    }
}
