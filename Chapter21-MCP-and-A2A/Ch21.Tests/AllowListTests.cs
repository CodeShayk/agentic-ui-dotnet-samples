using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Testing;
using Ch21.Beacon.Agent.Interop;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Ch21.Tests;

/// <summary>What Beacon takes from the MCP server's tool list, and what it leaves behind.</summary>
public sealed class AllowListTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_raw_McpClientTool_offers_whatever_the_server_lists_and_returns_unlabeled_results()
    {
        await using var systems = InteropSystems.Start();
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp"), EnableStandaloneGetStream = false },
            systems.Manuals.CreateClient(),
            ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct);

        // Every tool the server lists, the write tool included, each in the server's own words.
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        Assert.Equal(["search_manual", "submit_manual_correction", "get_manual_section"], tools.Select(t => t.Name));

        // A successful call comes back as plain model-ready text: no source, no notice, no size limit.
        AIFunction search = tools.Single(t => t.Name == "search_manual");
        var found = await search.InvokeAsync(new AIFunctionArguments { ["model"] = "RT-120", ["query"] = "locked rotor" }, Ct);
        Assert.StartsWith("2 section(s) of the Contoso Air RT-120 Service Manual", Assert.IsType<TextContent>(found).Text);

        // A failed call comes back as the whole CallToolResult: content blocks, isError and _meta naming the server.
        AIFunction read = tools.Single(t => t.Name == "get_manual_section");
        var failed = Assert.IsType<JsonElement>(await read.InvokeAsync(new AIFunctionArguments { ["sectionId"] = "XX/1.1" }, Ct));
        Assert.True(failed.GetProperty("isError").GetBoolean());
        Assert.Equal("contoso-manuals", failed.GetProperty("_meta").GetProperty("io.modelcontextprotocol/serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Beacon_offers_only_allow_listed_tools_in_its_own_words()
    {
        await using var systems = InteropSystems.Start();
        var source = systems.Beacon.Services.GetRequiredService<ManualsToolSource>();

        var tools = await source.GetToolsAsync(Ct);

        Assert.Equal(["get_manual_section", "search_manual"], tools.Select(t => t.Name).Order());
        foreach (var tool in tools.Cast<AIFunction>())
        {
            var pinned = ManualsToolSource.AllowedTools[tool.Name];
            Assert.Equal(pinned.Description, tool.Description);
            Assert.Equal(pinned.JsonSchema.GetRawText(), tool.JsonSchema.GetRawText());
        }

        Assert.Contains(systems.Beacon.Logs, l => l.Message == "Not offering MCP tool submit_manual_correction: it isn't on Beacon's allow-list.");
    }

    [Fact]
    public async Task A_client_declared_tool_cannot_take_a_remote_tools_name()
    {
        await using var systems = InteropSystems.Start();
        var input = AGUIWire.UserRun("Ask Northwind about the lead time for EXP-TANK-4.");
        input.Tools = [new AGUITool { Name = SupplierAgentTool.Name, Description = "A lookalike declared by the client." }];

        var events = await AGUIWire.RunAsync(systems.Beacon.CreateClient(), "/agent", input, Ct);

        // Beacon's tool ran on the server; a client-owned tool would have ended the run unanswered.
        Wire.AssertWellFormed(events);
        Assert.Equal(SupplierAgentTool.Source, Wire.Result(events, SupplierAgentTool.Name).GetProperty("source").GetString());
    }
}
