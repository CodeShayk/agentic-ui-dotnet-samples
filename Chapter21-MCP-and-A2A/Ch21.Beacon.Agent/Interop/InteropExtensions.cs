using AGUI.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ch21.Beacon.Agent.Interop;

/// <summary>Registration and pipeline extensions for the systems behind Beacon's agent.</summary>
public static class InteropExtensions
{
    /// <summary>How Northwind's agent appears on the AG-UI stream when Beacon delegates to it.</summary>
    public static SubagentInfo Supplier { get; } = new()
    {
        Name = "northwind-supply",
        Description = "Northwind Supply's parts agent, reached over A2A",
    };

    public static IServiceCollection AddBeaconInterop(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ManualsOptions>(configuration.GetSection(ManualsOptions.SectionName));
        services.Configure<SupplierOptions>(configuration.GetSection(SupplierOptions.SectionName));

        // Named clients, so each remote system has its own handler pipeline (and tests can swap it).
        services.AddHttpClient(InteropHttpClients.Manuals);
        services.AddHttpClient(InteropHttpClients.Supplier);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ManualsToolSource>();
        services.AddSingleton<SupplierAgentTool>();
        return services;
    }

    /// <summary>Adds the MCP and A2A tools to every run (see <see cref="InteropToolsChatClient"/>).</summary>
    public static ChatClientBuilder UseInteropTools(this ChatClientBuilder builder) =>
        builder.Use((inner, services) => new InteropToolsChatClient(
            inner,
            services.GetRequiredService<ManualsToolSource>(),
            services.GetRequiredService<SupplierAgentTool>().AsAIFunction()));

    /// <summary>Reports the supplier tool as a subagent (see <see cref="SubagentAttributionChatClient"/>).</summary>
    public static ChatClientBuilder UseSubagentAttribution(this ChatClientBuilder builder) =>
        builder.Use(inner => new SubagentAttributionChatClient(
            inner,
            new Dictionary<string, SubagentInfo> { [SupplierAgentTool.Name] = Supplier }));
}
