using System.Collections.Concurrent;
using Ch21.Beacon.Agent.Interop;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ch21.Tests;

/// <summary>One entry a host logged.</summary>
public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>Lets a test take a remote system offline: calls through the link fail as a refused connection would.</summary>
public sealed class RemoteLink
{
    public bool Down { get; set; }
}

/// <summary>
/// The chapter's three servers in memory: the manuals MCP server, Northwind's A2A agent and Beacon's
/// agent, with Beacon's named HTTP clients routed to the other two through switchable links.
/// </summary>
public sealed class InteropSystems : IAsyncDisposable
{
    private InteropSystems(IReadOnlyDictionary<string, string?> settings)
    {
        // The remote systems run quietly; the tests judge them through Beacon.
        Manuals = new WebApplicationFactory<global::Ch21.ManualsMcpServer.Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Testing").ConfigureLogging(l => l.ClearProviders()));
        Supplier = new WebApplicationFactory<global::Ch21.SupplierAgent.Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Testing").ConfigureLogging(l => l.ClearProviders()));
        Beacon = new BeaconHost(settings, this);
    }

    public WebApplicationFactory<global::Ch21.ManualsMcpServer.Program> Manuals { get; }

    public WebApplicationFactory<global::Ch21.SupplierAgent.Program> Supplier { get; }

    public BeaconHost Beacon { get; }

    public RemoteLink ManualsLink { get; } = new();

    public RemoteLink SupplierLink { get; } = new();

    /// <summary>Starts the three systems; settings override Beacon's configuration.</summary>
    public static InteropSystems Start(params (string Key, string? Value)[] settings)
    {
        var merged = new Dictionary<string, string?>
        {
            ["AI:Provider"] = "Scripted",
            ["AI:ScriptedChunkDelayMilliseconds"] = "0",
        };

        foreach (var (key, value) in settings)
        {
            merged[key] = value;
        }

        return new InteropSystems(merged);
    }

    public async ValueTask DisposeAsync()
    {
        await Beacon.DisposeAsync();
        await Manuals.DisposeAsync();
        await Supplier.DisposeAsync();
    }

    public sealed class BeaconHost(IReadOnlyDictionary<string, string?> settings, InteropSystems systems)
        : WebApplicationFactory<global::Ch21.Beacon.Agent.Program>
    {
        private readonly CapturingLoggerProvider _logs = new();

        public IReadOnlyList<LogEntry> Logs => _logs.Entries;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));

            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            // Route Beacon's clients for the other systems to their in-memory servers.
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient(InteropHttpClients.Manuals)
                    .ConfigurePrimaryHttpMessageHandler(() => new SwitchableHandler(systems.ManualsLink) { InnerHandler = systems.Manuals.Server.CreateHandler() })
                    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

                services.AddHttpClient(InteropHttpClients.Supplier)
                    .ConfigurePrimaryHttpMessageHandler(() => new SwitchableHandler(systems.SupplierLink) { InnerHandler = systems.Supplier.Server.CreateHandler() })
                    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
            });
        }
    }

    private sealed class SwitchableHandler(RemoteLink link) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            link.Down
                ? throw new HttpRequestException($"No connection could be made to {request.RequestUri?.Authority} (the test took it offline).")
                : base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Keeps every log entry in memory so a test can assert on what the server recorded.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception));
            }
        }
    }
}
