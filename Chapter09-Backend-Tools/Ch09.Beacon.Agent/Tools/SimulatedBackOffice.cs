using System.Net;
using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch09.Beacon.Agent.Tools;

/// <summary>
/// Stands in for Contoso Climate's back-office systems. Beacon's tools read an in-memory store that
/// answers in microseconds; the real work-order system, parts catalog and warranty service take a
/// second or two, and the warranty service is sometimes down. This adds both, from configuration, so
/// a tool call in progress and a tool call that fails can be seen on the wire and in the Desk.
/// </summary>
public sealed class SimulatedBackOffice(
    IReadOnlyDictionary<string, TimeSpan> latency,
    IReadOnlySet<string> warrantyServiceDownFor)
{
    public const string SectionName = "BackOffice";

    /// <summary>No latency and no outages, for tests that only care about the events.</summary>
    public static SimulatedBackOffice Instant { get; } = new(new Dictionary<string, TimeSpan>(), new HashSet<string>());

    /// <summary>Reads <c>BackOffice:LatencyMilliseconds:{tool}</c> and <c>BackOffice:WarrantyServiceDownFor</c>.</summary>
    public static SimulatedBackOffice FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        var latency = section.GetSection("LatencyMilliseconds").GetChildren()
            .Where(entry => int.TryParse(entry.Value, out var ms) && ms > 0)
            .ToDictionary(entry => entry.Key, entry => TimeSpan.FromMilliseconds(int.Parse(entry.Value!)), StringComparer.Ordinal);

        var down = section.GetSection("WarrantyServiceDownFor").GetChildren()
            .Select(entry => entry.Value)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new SimulatedBackOffice(latency, down);
    }

    /// <summary>How long the system behind <paramref name="toolName"/> takes to answer.</summary>
    public TimeSpan LatencyOf(string toolName) => latency.GetValueOrDefault(toolName, TimeSpan.Zero);

    /// <summary>Puts the simulated system in front of a tool.</summary>
    public AIFunction Wrap(AIFunction tool) => new BackOfficeCall(tool, this);

    private sealed class BackOfficeCall(AIFunction tool, SimulatedBackOffice backOffice) : DelegatingAIFunction(tool)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            await Task.Delay(backOffice.LatencyOf(Name), cancellationToken);

            if (Name == BeaconTools.CheckWarrantyName
                && arguments.TryGetValue("equipmentId", out var equipmentId)
                && backOffice.IsWarrantyServiceDownFor(equipmentId?.ToString()))
            {
                // What an outage looks like from inside: an exception full of internal detail that
                // neither the model nor the dispatcher should ever see.
                throw new HttpRequestException(
                    $"GET https://warranty-api.contoso-air.internal/v2/units/{equipmentId} failed: 503 Service Unavailable " +
                    "(upstream warranty-db-02.contoso-air.internal:5432 not responding).",
                    inner: null,
                    HttpStatusCode.ServiceUnavailable);
            }

            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }
    }

    private bool IsWarrantyServiceDownFor(string? equipmentId) =>
        equipmentId is not null && warrantyServiceDownFor.Contains(equipmentId);
}
