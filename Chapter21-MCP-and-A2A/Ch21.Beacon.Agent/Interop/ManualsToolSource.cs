using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Ch21.Beacon.Agent.Interop;

/// <summary>A remote tool Beacon has reviewed: the description and schema Beacon shows its model.</summary>
public sealed record PinnedTool(string Description, JsonElement JsonSchema);

/// <summary>
/// Connects to the manuals MCP server, lists its tools and keeps the ones on Beacon's allow-list,
/// each wrapped in a <see cref="LabeledMcpTool"/>. Connection failures never fail a run: Beacon
/// offers no manual tools until the server answers again.
/// </summary>
public sealed partial class ManualsToolSource(
    IHttpClientFactory httpClientFactory,
    IOptions<ManualsOptions> options,
    ILoggerFactory loggerFactory,
    TimeProvider timeProvider) : IAsyncDisposable
{
    public const string Source = "Equipment manuals (MCP)";

    /// <summary>
    /// The only MCP tools Beacon offers its model, declared in Beacon's own words. The server's
    /// descriptions are model-facing text from another system, so they never reach the model.
    /// </summary>
    public static IReadOnlyDictionary<string, PinnedTool> AllowedTools { get; } = new Dictionary<string, PinnedTool>(StringComparer.Ordinal)
    {
        ["search_manual"] = Pin(
            "Searches the service manual for one equipment model and returns matching section IDs and titles.",
            """{"type":"object","properties":{"model":{"type":"string","description":"CA-36X, HP-24S, RT-120 or FX-90."},"query":{"type":"string","description":"Words to look for, for example 'locked rotor'."}},"required":["model","query"]}"""),
        ["get_manual_section"] = Pin(
            "Gets the text of one manual section by ID, for example RT-120/7.3. The text comes from outside Contoso Climate.",
            """{"type":"object","properties":{"sectionId":{"type":"string","description":"A section ID returned by search_manual, for example RT-120/7.3."}},"required":["sectionId"]}"""),
    };

    private readonly ILogger _logger = loggerFactory.CreateLogger<ManualsToolSource>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Connection? _connection;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

    private sealed record Connection(McpClient Client, IReadOnlyList<AITool> Tools);

    /// <summary>The allow-listed manual tools, or none while the server is unreachable.</summary>
    public async ValueTask<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _connection) is { } current)
        {
            return current.Tools;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { } existing)
            {
                return existing.Tools;
            }

            if (timeProvider.GetUtcNow() < _retryAt)
            {
                return [];
            }

            _connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            return _connection.Tools;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _retryAt = timeProvider.GetUtcNow().AddSeconds(options.Value.RetryAfterSeconds);
            LogUnavailable(_logger, ex, options.Value.Endpoint);
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Connection> ConnectAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = options.Value.Endpoint,
                Name = "contoso-manuals",
                TransportMode = HttpTransportMode.StreamableHttp,
                EnableStandaloneGetStream = false,
            },
            httpClientFactory.CreateClient(InteropHttpClients.Manuals),
            loggerFactory,
            ownsHttpClient: true);

        // No sampling, elicitation or roots handlers: the server can't borrow Beacon's model or ask its users anything.
        var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions { ClientInfo = new() { Name = "beacon-agent", Version = "21.0.0" } },
            loggerFactory,
            timeout.Token).ConfigureAwait(false);

        try
        {
            List<AITool> offered = [];
            foreach (var tool in await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false))
            {
                if (!AllowedTools.TryGetValue(tool.Name, out var pinned))
                {
                    LogNotAllowed(_logger, tool.Name);
                }
                else if (!SameParameters(tool.JsonSchema, pinned.JsonSchema))
                {
                    LogContractChanged(_logger, tool.Name);
                }
                else
                {
                    offered.Add(new LabeledMcpTool(tool, pinned, this, options.Value, _logger));
                }
            }

            LogConnected(_logger, options.Value.Endpoint, offered.Count);
            return new Connection(client, offered);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Drops a connection whose calls fail, so the next run reconnects.</summary>
    internal async ValueTask ResetAsync()
    {
        Connection? stale;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            stale = _connection;
            _connection = null;
            _retryAt = timeProvider.GetUtcNow().AddSeconds(options.Value.RetryAfterSeconds);
        }
        finally
        {
            _gate.Release();
        }

        if (stale is not null)
        {
            await stale.Client.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _connection, null) is { } connection)
        {
            await connection.Client.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    // A server that renames or adds parameters has changed its contract; Beacon stops offering
    // the tool until someone reviews the change and updates the pin.
    private static bool SameParameters(JsonElement served, JsonElement pinned) =>
        Names(served, "properties").SetEquals(Names(pinned, "properties")) &&
        Names(served, "required").SetEquals(Names(pinned, "required"));

    private static HashSet<string> Names(JsonElement schema, string property) =>
        !schema.TryGetProperty(property, out var value) ? []
        : value.ValueKind == JsonValueKind.Object ? [.. value.EnumerateObject().Select(p => p.Name)]
        : value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray().Select(e => e.GetString() ?? "")]
        : [];

    private static PinnedTool Pin(string description, string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return new PinnedTool(description, document.RootElement.Clone());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to the manuals MCP server at {Endpoint}; offering {Count} tool(s).")]
    private static partial void LogConnected(ILogger logger, Uri endpoint, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Not offering MCP tool {Tool}: it isn't on Beacon's allow-list.")]
    private static partial void LogNotAllowed(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not offering MCP tool {Tool}: its parameters no longer match the pinned contract.")]
    private static partial void LogContractChanged(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The manuals MCP server at {Endpoint} is unavailable; runs continue without manual tools.")]
    private static partial void LogUnavailable(ILogger logger, Exception exception, Uri endpoint);
}
