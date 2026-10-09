using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ch13.Tests.Infrastructure;

/// <summary>
/// Ch13.Beacon.Agent in memory, exactly as its <c>Program.Main</c> builds it, with every delay at zero so
/// runs are fast and deterministic. Tests that need another model replace <see cref="Ch13.Beacon.Agent.Program.ModelKey"/>.
/// </summary>
public sealed class AgentHost(IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Ch13.Beacon.Agent.Program>
{
    public const string Diagnose = "Diagnose WO-1001 and draft the service report.";

    private static readonly Dictionary<string, string?> Baseline = new()
    {
        ["AI:Provider"] = "Scripted",
        ["AI:ScriptedChunkDelayMilliseconds"] = "0",
        ["Beacon:ToolDelayMilliseconds"] = "0",
        ["Beacon:ReportSaveDelayMilliseconds"] = "0",
        ["Beacon:ArgumentFragmentDelayMilliseconds"] = "0",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in Baseline.Concat(settings ?? new Dictionary<string, string?>()))
        {
            builder.UseSetting(key, value);
        }

        if (services is not null)
        {
            builder.ConfigureServices(services);
        }
    }

    /// <summary>Runs one user turn and returns the events.</summary>
    public Task<IReadOnlyList<BaseEvent>> RunAsync(string text = Diagnose, JsonElement? state = null)
    {
        var input = AGUIWire.UserRun(text, "t-13", "r-1");
        input.State = state;
        return AGUIWire.RunAsync(CreateClient(), "/agent", input, TestContext.Current.CancellationToken);
    }

    /// <summary>Runs one user turn and returns the frames exactly as written.</summary>
    public Task<IReadOnlyList<JsonElement>> RunRawAsync(string text = Diagnose) =>
        AGUIWire.RunRawAsync(CreateClient(), "/agent", AGUIWire.UserRun(text, "t-13", "r-1"), TestContext.Current.CancellationToken);
}
