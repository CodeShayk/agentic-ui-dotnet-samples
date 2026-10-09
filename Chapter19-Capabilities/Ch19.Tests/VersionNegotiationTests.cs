using System.Net;
using System.Text.Json;
using Beacon.Testing;
using Ch19.Beacon.Adaptive.Versioning;
using Ch19.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Ch19.Tests;

/// <summary>
/// The version exchange on both sides: the client's declaration on <c>RunAgentInput</c>, the agent's own
/// on <c>RUN_STARTED</c>, and what each side does when the other is newer, older or unreadable.
/// </summary>
public sealed class VersionNegotiationTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("1.0")]
    [InlineData(null)]     // a peer from before the field existed
    [InlineData("1.4")]    // a newer minor of the 1.x line
    [InlineData("1.0.0")]  // not a version identifier: handled like a newer one
    public async Task RUN_STARTED_declares_the_agents_own_version_whatever_the_client_declared(string? declared)
    {
        await using var host = new AgentHost();
        var input = AGUIWire.UserRun("Hello", "t-19", "r-1");
        input.ProtocolVersion = declared;

        var frames = await AGUIWire.RunRawAsync(host.CreateClient(), "/agent", input, Cancel);

        var started = frames[0];
        Assert.Equal("RUN_STARTED", started.GetProperty("type").GetString());
        Assert.Equal("1.0", started.GetProperty("protocolVersion").GetString()); // never an echo of the input
        Assert.Equal("RUN_FINISHED", frames[^1].GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("1.0", null)]
    [InlineData(null, 1901)]
    [InlineData("1.4", 1903)]
    [InlineData("latest", 1903)]
    public async Task The_agent_serves_and_logs_what_it_noticed(string? declared, int? eventId)
    {
        await using var host = new AgentHost();
        var input = AGUIWire.UserRun("Hello", "t-19", "r-1");
        input.ProtocolVersion = declared;

        await AGUIWire.RunRawAsync(host.CreateClient(), "/agent", input, Cancel);

        var versionLogs = host.Logs.Entries.Where(e => e.EventId is >= 1901 and <= 1904).ToList();
        if (eventId is null)
        {
            Assert.Empty(versionLogs);
        }
        else
        {
            var entry = Assert.Single(versionLogs);
            Assert.Equal(eventId, entry.EventId);
            Assert.Equal(eventId == 1903 ? LogLevel.Warning : LogLevel.Information, entry.Level);
        }
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("0.9")]
    public async Task A_major_line_the_agent_doesnt_implement_is_refused_before_RUN_STARTED(string declared)
    {
        await using var host = new AgentHost();
        var input = AGUIWire.UserRun("Hello", "t-19", "r-1");
        input.ProtocolVersion = declared;

        using var response = await ClientStack.PostAsync(host.CreateClient(), input);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal("Unsupported AG-UI protocol version", problem.RootElement.GetProperty("title").GetString());
        Assert.Contains(declared, problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(LogLevel.Warning, Assert.Single(host.Logs.WithEventId(1904)).Level);
    }

    [Fact]
    public async Task The_stock_glue_leaves_protocolVersion_out_which_is_what_this_chapter_fixes()
    {
        await using var stock = await StockAgentHost.StartAsync();

        var frames = await AGUIWire.RunRawAsync(stock.CreateClient(), "/agent", AGUIWire.UserRun("Hello", "t-19", "r-1"), Cancel);

        Assert.Equal("RUN_STARTED", frames[0].GetProperty("type").GetString());
        Assert.False(frames[0].TryGetProperty("protocolVersion", out _));
    }

    [Fact]
    public async Task A_client_reads_the_declaration_from_the_run_and_judges_it()
    {
        await using var host = new AgentHost();
        await using var stock = await StockAgentHost.StartAsync();
        using var current = ClientStack.Conversation(host.CreateClient(), host.Endpoint);
        using var older = ClientStack.Conversation(stock.CreateClient(), stock.Endpoint);

        await current.SendAsync("Hello", cancellationToken: Cancel);
        await older.SendAsync("Hello", cancellationToken: Cancel);

        Assert.Equal("1.0", current.View.CurrentRun!.ProtocolVersion);
        Assert.Equal(VersionVerdict.Same, ProducerVersionCheck.From(current.View.CurrentRun.ProtocolVersion).Verdict);
        Assert.Null(older.View.CurrentRun!.ProtocolVersion);
        var check = ProducerVersionCheck.From(older.View.CurrentRun.ProtocolVersion);
        Assert.Equal(VersionVerdict.Absent, check.Verdict);
        Assert.False(check.IsWarning); // an older peer is expected; the client just carries on
    }
}
