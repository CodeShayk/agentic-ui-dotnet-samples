using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Ch25.Tests;

/// <summary>One entry the host logged, with the exception it carried, as a provider would print it.</summary>
public sealed record LogEntry(string Category, LogLevel Level, string Message, string? Exception = null);

/// <summary>
/// Hosts Ch25.Beacon.Agent exactly as its <c>Program.Main</c> builds it, on the scripted model with
/// no streaming delay, and adds OpenTelemetry's in-memory exporters next to the app's own pipeline.
/// The console exporter is off. Pass <c>kestrel: true</c> when a test needs real HTTP between spans:
/// the fault archive call goes to the app's own listening address, which the in-memory TestServer
/// doesn't have.
/// </summary>
public sealed class TracedAgentHost(string environment, (string Key, string? Value)[] settings)
    : WebApplicationFactory<global::Ch25.Beacon.Agent.Program>
{
    private readonly LockedCollection<Activity> _spans = new();
    private readonly LockedCollection<MetricSnapshot> _metrics = new();
    private readonly ConcurrentQueue<LogEntry> _logs = new();

    public IReadOnlyList<Activity> Spans => _spans.Snapshot();

    public IReadOnlyList<LogEntry> Logs => [.. _logs];

    public static TracedAgentHost Start(string environment = "Testing", bool kestrel = false, params (string Key, string? Value)[] settings)
    {
        var host = new TracedAgentHost(environment, settings);
        if (kestrel)
        {
            host.UseKestrel(0);
        }

        host.StartServer();
        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("AI:Provider", "Scripted");
        builder.UseSetting("AI:ScriptedChunkDelayMilliseconds", "0");
        builder.UseSetting("Beacon:Telemetry:ConsoleExporter", "false");
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(_logs)));

        builder.ConfigureTestServices(services =>
        {
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(_spans));
            services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddInMemoryExporter(_metrics));
        });
    }

    /// <summary>Collects the metrics recorded so far and returns the snapshots.</summary>
    public IReadOnlyList<MetricSnapshot> CollectMetrics()
    {
        _metrics.Clear();
        Services.GetRequiredService<MeterProvider>().ForceFlush();
        return _metrics.Snapshot();
    }

    /// <summary>
    /// Waits until the trace that holds the given AG-UI run has ended, which is when the server's
    /// request span (the last to end) has been exported, and returns every span in that trace.
    /// </summary>
    public async Task<IReadOnlyList<Activity>> WaitForTraceAsync(string runId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var spans = Spans;
            if (spans.FirstOrDefault(s => s.OperationName == "agui.run" && Equals(s.GetTagItem("agui.run_id"), runId)) is { } run)
            {
                var trace = spans.Where(s => s.TraceId == run.TraceId).ToList();
                if (trace.Any(s => s.Kind == ActivityKind.Server && s.DisplayName == "POST /agent"))
                {
                    return trace;
                }
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException($"The trace for run {runId} was not exported in time.");
    }

    /// <summary>A thread-safe collection for exporters that add from whichever thread ends a span.</summary>
    private sealed class LockedCollection<T> : ICollection<T>
    {
        private readonly List<T> _items = [];

        public int Count
        {
            get { lock (_items) { return _items.Count; } }
        }

        public bool IsReadOnly => false;

        public void Add(T item)
        {
            lock (_items)
            {
                _items.Add(item);
            }
        }

        public void Clear()
        {
            lock (_items)
            {
                _items.Clear();
            }
        }

        public bool Contains(T item)
        {
            lock (_items)
            {
                return _items.Contains(item);
            }
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            lock (_items)
            {
                _items.CopyTo(array, arrayIndex);
            }
        }

        public bool Remove(T item)
        {
            lock (_items)
            {
                return _items.Remove(item);
            }
        }

        public IReadOnlyList<T> Snapshot()
        {
            lock (_items)
            {
                return [.. _items];
            }
        }

        public IEnumerator<T> GetEnumerator() => Snapshot().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Keeps every log entry the app's filters let through, whatever its level.</summary>
    private sealed class CapturingLoggerProvider(ConcurrentQueue<LogEntry> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception?.ToString()));
        }
    }
}
