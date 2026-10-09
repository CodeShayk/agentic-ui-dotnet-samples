using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch04.Tests;

/// <summary>
/// Hosts Ch04.Beacon.Agent in memory, exactly as Program.cs builds it, with the model swapped for a
/// deterministic one. The last <see cref="IChatClient"/> registration wins.
/// </summary>
public sealed class AgentFactory(IChatClient model) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services => services.AddChatClient(model));
}
