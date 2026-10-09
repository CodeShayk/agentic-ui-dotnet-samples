using System.ComponentModel;
using Beacon.AI;
using Beacon.Domain;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ch25.Beacon.Agent.FaultArchive;

/// <summary>Where Contoso's fault archive lives and, for the stand-in, how slowly it answers.</summary>
public sealed class FaultArchiveOptions
{
    public const string SectionName = "Beacon:FaultArchive";

    /// <summary>The archive's address. Empty means the stand-in this app hosts at <c>/archive</c>.</summary>
    public string? BaseAddress { get; set; }

    /// <summary>How long the stand-in waits before answering: the slow dependency of section 25.7.</summary>
    public int DelayMilliseconds { get; set; }
}

/// <summary>
/// Reads fault history from Contoso's equipment archive over HTTP. In production the archive is a
/// separate service; the companion code hosts a stand-in in the same app so the sample runs alone.
/// Because the call goes through <see cref="HttpClient"/>, it appears in Beacon's traces as a client
/// span with the archive's own server span beneath it.
/// </summary>
public sealed class FaultArchiveClient(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "FaultArchive";

    public async Task<IReadOnlyList<FaultRecord>> GetFaultHistoryAsync(string equipmentId, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        return await http.GetFromJsonAsync(
            $"archive/faults/{Uri.EscapeDataString(equipmentId)}",
            BeaconJsonContext.Default.IReadOnlyListFaultRecord,
            cancellationToken) ?? [];
    }
}

/// <summary>Beacon's <c>get_fault_history</c> tool, backed by the archive instead of the in-memory store.</summary>
public static class FaultArchiveTool
{
    public static AIFunction Create() => AIFunctionFactory.Create(
        GetFaultHistoryAsync,
        BeaconTools.GetFaultHistoryName,
        "Gets the recorded fault codes for a piece of equipment.",
        BeaconJson.Options);

    // The IServiceProvider parameter is bound from the function-invoking client's services, not
    // from the model's arguments, so it doesn't appear in the tool's schema.
    private static Task<IReadOnlyList<FaultRecord>> GetFaultHistoryAsync(
        [Description("The equipment ID, for example EQ-501.")] string equipmentId,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        services.GetRequiredService<FaultArchiveClient>().GetFaultHistoryAsync(equipmentId, cancellationToken);
}

public static class FaultArchiveExtensions
{
    /// <summary>Registers the archive client and its named <see cref="HttpClient"/>.</summary>
    public static IServiceCollection AddFaultArchive(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FaultArchiveOptions>(configuration.GetSection(FaultArchiveOptions.SectionName));
        services.AddSingleton<FaultArchiveClient>();
        services.AddHttpClient(FaultArchiveClient.HttpClientName, (sp, http) =>
        {
            var configured = sp.GetRequiredService<IOptions<FaultArchiveOptions>>().Value.BaseAddress;

            // No address configured: call the stand-in on this app's own listening address.
            var address = !string.IsNullOrWhiteSpace(configured)
                ? configured
                : sp.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                    ?? "http://localhost";

            http.BaseAddress = new Uri(address.EndsWith('/') ? address : address + "/");
        });

        return services;
    }

    /// <summary>Maps the stand-in archive: <c>GET /archive/faults/{equipmentId}</c>, with a configurable delay.</summary>
    public static IEndpointConventionBuilder MapFaultArchive(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/archive/faults/{equipmentId}", async (
            string equipmentId,
            IFieldServiceStore store,
            IOptionsMonitor<FaultArchiveOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (options.CurrentValue.DelayMilliseconds is > 0 and var delay)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return TypedResults.Json(store.GetFaultHistory(equipmentId), BeaconJsonContext.Default.IReadOnlyListFaultRecord);
        });
}
