using System.Net;
using AGUI.Abstractions;

namespace Ch19.Beacon.Adaptive.Capabilities;

/// <summary>How a discovery attempt ended.</summary>
public enum DiscoveryOutcome
{
    /// <summary>The agent served a valid declaration.</summary>
    Declared,

    /// <summary>The agent has no capabilities document at that address (404 or 405).</summary>
    NotOffered,

    /// <summary>The document couldn't be fetched: network failure, timeout or a server error.</summary>
    Unreachable,

    /// <summary>The agent served a document that failed validation, so it isn't used.</summary>
    Rejected,
}

/// <summary>What a client learned about an agent before its first run.</summary>
/// <param name="Outcome">How discovery ended.</param>
/// <param name="Capabilities">The declaration when <see cref="Outcome"/> is <see cref="DiscoveryOutcome.Declared"/>; otherwise <see langword="null"/>.</param>
/// <param name="Notes">Problems and unrecognized members, for diagnostics. Never shown as errors to the user.</param>
public sealed record CapabilityDiscoveryResult(
    DiscoveryOutcome Outcome,
    AgentCapabilities? Capabilities,
    IReadOnlyList<string> Notes)
{
    /// <summary>Nothing learned: the client proceeds as it would for an agent that declared nothing.</summary>
    public static CapabilityDiscoveryResult Nothing(DiscoveryOutcome outcome, params string[] notes) => new(outcome, null, notes);
}

/// <summary>
/// Fetches Beacon's capabilities document. AG-UI 1.0 defines the document's shape but not how to
/// obtain it, so the address is Beacon's own convention: <c>GET {agent endpoint}/capabilities</c>.
/// Discovery never blocks a run. Every failure becomes "declared nothing", which is what the
/// specification asks a consumer to fall back to.
/// </summary>
public sealed class CapabilityDiscovery(HttpClient http, Uri documentUri)
{
    /// <summary>How long discovery may hold up the first screen.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The conventional document address for an AG-UI endpoint: <c>/agent</c> becomes <c>/agent/capabilities</c>.</summary>
    public static Uri DocumentFor(Uri agentEndpoint) =>
        new(agentEndpoint.AbsoluteUri.TrimEnd('/') + "/capabilities");

    public async Task<CapabilityDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync(documentUri, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return CapabilityDiscoveryResult.Nothing(DiscoveryOutcome.NotOffered, $"{documentUri} answered {(int)response.StatusCode}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return CapabilityDiscoveryResult.Nothing(DiscoveryOutcome.Unreachable, $"{documentUri} answered {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            var read = CapabilityReader.Read(body);
            return read.IsUsable
                ? new CapabilityDiscoveryResult(DiscoveryOutcome.Declared, read.Capabilities, [.. read.Unrecognized.Select(p => $"unrecognized member {p} ignored")])
                : new CapabilityDiscoveryResult(DiscoveryOutcome.Rejected, null, read.Problems);
        }
        catch (HttpRequestException ex)
        {
            return CapabilityDiscoveryResult.Nothing(DiscoveryOutcome.Unreachable, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CapabilityDiscoveryResult.Nothing(DiscoveryOutcome.Unreachable, $"No answer within {Timeout.TotalSeconds:0} seconds.");
        }
    }
}
