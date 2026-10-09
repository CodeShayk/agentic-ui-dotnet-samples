using System.Collections.Concurrent;
using Ch19.Beacon.Agent.Capabilities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ch19.Tests.Infrastructure;

/// <summary>
/// Ch19.Beacon.Agent in memory, exactly as its <c>Program.Main</c> builds it, in the given profile, on
/// the scripted model with no delays, or on <paramref name="model"/> when one is given. Every log entry is
/// kept for assertions.
/// </summary>
public sealed class AgentHost(CapabilityProfile profile = CapabilityProfile.Full, IChatClient? model = null) : WebApplicationFactory<Ch19.Beacon.Agent.Program>
{
    public LogCollector Logs { get; } = new();

    /// <summary>The agent's AG-UI endpoint as an absolute URI on the test server.</summary>
    public Uri Endpoint => new(Server.BaseAddress, "agent");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("AI:Provider", "Scripted");
        builder.UseSetting("AI:ScriptedChunkDelayMilliseconds", "0");
        builder.UseSetting("Beacon:Profile", profile.ToString());
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        if (model is not null)
        {
            builder.ConfigureTestServices(services => services.AddKeyedSingleton(Ch19.Beacon.Agent.Program.ModelKey, model));
        }
    }
}

/// <summary>One log entry.</summary>
public sealed record LogEntry(string Category, LogLevel Level, int EventId, string Message);

/// <summary>Keeps every log entry written through it.</summary>
public sealed class LogCollector : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public IEnumerable<LogEntry> WithEventId(int eventId) => _entries.Where(e => e.EventId == eventId);

    public ILogger CreateLogger(string categoryName) => new Collector(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Collector(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, eventId.Id, formatter(state, exception)));
    }
}
