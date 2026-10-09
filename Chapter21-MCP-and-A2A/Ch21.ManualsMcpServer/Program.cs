using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace Ch21.ManualsMcpServer;

// Program lives in a namespace so the chapter's test project can host all three servers side by side.
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // An MCP server over Streamable HTTP. Stateless: any instance can answer any request, with no
        // session affinity, which is all a read-mostly manual library behind a load balancer needs.
        builder.Services
            .AddMcpServer(options => options.ServerInfo = new Implementation
            {
                Name = "contoso-manuals",
                Title = "Contoso Climate equipment manuals",
                Version = "1.0.0",
            })
            .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<ManualTools>();

        var app = builder.Build();

        // POST /mcp: JSON-RPC in, JSON or SSE out.
        app.MapMcp("/mcp");

        app.Run();
    }
}
