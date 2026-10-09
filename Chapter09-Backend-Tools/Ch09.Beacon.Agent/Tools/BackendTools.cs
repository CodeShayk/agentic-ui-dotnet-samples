using Beacon.AI;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch09.Beacon.Agent.Tools;

/// <summary>
/// Every tool the agent runs itself: Beacon's six read-only tools and this chapter's checklist tool,
/// each behind the simulated back office and, unless configuration turns it off, an error boundary.
/// Built once; the pipeline puts the same instances on every request's <see cref="ChatOptions"/>.
/// </summary>
public sealed class BackendTools
{
    private readonly HashSet<string> _names;

    public BackendTools(
        IFieldServiceStore store,
        SimulatedBackOffice backOffice,
        CurrentUser user,
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        var guard = configuration.GetValue("Beacon:ToolErrorBoundary", true);
        var logger = loggerFactory.CreateLogger<ToolErrorBoundary>();

        IEnumerable<AIFunction> tools =
        [
            .. new BeaconTools(store).CreateReadOnlyTools().OfType<AIFunction>(),
            new ChecklistTool(store, () => user).Create(),
        ];

        Tools = [.. tools
            .Select(backOffice.Wrap)
            .Select(tool => guard ? new ToolErrorBoundary(tool, SystemBehind(tool.Name), logger) : tool)];

        _names = [.. Tools.Select(t => t.Name)];
    }

    /// <summary>The server's tools, in the order the model is offered them.</summary>
    public IReadOnlyList<AIFunction> Tools { get; }

    /// <summary>
    /// Puts the server's instructions and tools on a request. Tools the client declared (Chapter 10)
    /// are kept unless one borrows a server tool's name.
    /// </summary>
    public void Configure(ChatOptions options)
    {
        options.Instructions = BeaconInstructions.Default;
        options.Tools = [.. Tools, .. (options.Tools ?? []).Where(t => !_names.Contains(t.Name))];
    }

    // How a dispatcher would name the system a tool depends on, for error messages.
    private static string SystemBehind(string toolName) => toolName switch
    {
        BeaconTools.SearchPartsName => "the parts catalog",
        BeaconTools.CheckWarrantyName => "the warranty service",
        BeaconTools.GetFaultHistoryName => "the fault log",
        BeaconTools.GetScheduleName => "the scheduling system",
        _ => "the work-order system",
    };
}
