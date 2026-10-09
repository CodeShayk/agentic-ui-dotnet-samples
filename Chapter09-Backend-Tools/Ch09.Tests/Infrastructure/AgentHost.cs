using System.Collections.Concurrent;
using AGUI.Abstractions;
using Beacon.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ch09.Tests.Infrastructure;

/// <summary>One entry the host logged.</summary>
public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Ch09.Beacon.Agent in memory, exactly as its <c>Program.Main</c> builds it, with configuration
/// overrides on top. The baseline is the scripted model with every delay at zero, so tests are fast
/// and deterministic; a test that wants latency sets it.
/// </summary>
public sealed class AgentHost(IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Ch09.Beacon.Agent.Program>
{
    private static readonly Dictionary<string, string?> Baseline = new()
    {
        ["AI:Provider"] = "Scripted",
        ["AI:ScriptedChunkDelayMilliseconds"] = "0",
        ["Beacon:ArgumentFragmentDelayMilliseconds"] = "0",
        ["BackOffice:LatencyMilliseconds:get_work_order"] = "0",
        ["BackOffice:LatencyMilliseconds:list_work_orders"] = "0",
        ["BackOffice:LatencyMilliseconds:search_parts"] = "0",
        ["BackOffice:LatencyMilliseconds:check_warranty"] = "0",
        ["BackOffice:LatencyMilliseconds:get_fault_history"] = "0",
        ["BackOffice:LatencyMilliseconds:get_schedule"] = "0",
        ["BackOffice:LatencyMilliseconds:set_checklist_item"] = "0",
    };

    private readonly CapturingLoggerProvider _logs = new();

    /// <summary>Everything the host logged at Information or above.</summary>
    public IReadOnlyList<LogEntry> Logs => [.. _logs.Entries];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.AddProvider(_logs));

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
    public Task<IReadOnlyList<BaseEvent>> RunAsync(string text, string threadId = "t-1", string runId = "r-1") =>
        AGUIWire.RunAsync(CreateClient(), "/agent", AGUIWire.UserRun(text, threadId, runId), TestContext.Current.CancellationToken);

    /// <summary>Runs one user turn and returns the frames exactly as written.</summary>
    public Task<IReadOnlyList<System.Text.Json.JsonElement>> RunRawAsync(string text) =>
        AGUIWire.RunRawAsync(CreateClient(), "/agent", AGUIWire.UserRun(text), TestContext.Current.CancellationToken);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
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
}
