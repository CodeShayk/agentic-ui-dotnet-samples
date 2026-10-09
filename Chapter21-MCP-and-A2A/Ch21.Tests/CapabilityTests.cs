using System.Text.Json;
using Ch21.Beacon.Agent.Interop;

namespace Ch21.Tests;

/// <summary>
/// Chapter 19's capability document on this checkpoint: the tools Beacon executes, remote ones included, in
/// Beacon's words, and Northwind's agent as a subagent Beacon delegates to.
/// </summary>
public sealed class CapabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Remote_tools_are_declared_as_the_agents_own_in_Beacons_words()
    {
        await using var systems = InteropSystems.Start();
        systems.ManualsLink.Down = true; // the document doesn't depend on the manual library being reachable

        using var document = JsonDocument.Parse(await systems.Beacon.CreateClient().GetStringAsync("/agent/capabilities", Ct));
        var items = document.RootElement.GetProperty("tools").GetProperty("items").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("description").GetString());

        Assert.Contains("get_work_order", items.Keys); // Beacon's own tools
        Assert.Equal(ManualsToolSource.AllowedTools["get_manual_section"].Description, items["get_manual_section"]);
        Assert.Equal(ManualsToolSource.AllowedTools["search_manual"].Description, items["search_manual"]);
        Assert.Contains(SupplierAgentTool.Name, items.Keys);
        Assert.DoesNotContain("submit_manual_correction", items.Keys);
        Assert.DoesNotContain(items.Values, d => d == "Gets the full text of one manual section by its ID, for example RT-120/7.3."); // the server's words

        var multiAgent = document.RootElement.GetProperty("multiAgent");
        Assert.True(multiAgent.GetProperty("delegation").GetBoolean());
        Assert.Equal("northwind-supply", Assert.Single(multiAgent.GetProperty("subagents").EnumerateArray()).GetProperty("name").GetString());
    }
}
