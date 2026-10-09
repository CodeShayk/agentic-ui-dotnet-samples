using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ch23.Tests.Infrastructure;

/// <summary>A clock the tests can move forward, for approval expiry.</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>One entry the host logged, after redaction.</summary>
public sealed record LogEntry(string Category, LogLevel Level, int EventId, string Message);

/// <summary>
/// Hosts one of the chapter's agents in memory exactly as its <c>Program.Main</c> builds it, with the
/// test signing key, a recording model, a manual clock and captured logs.
/// </summary>
public sealed class BeaconHost<TProgram> : WebApplicationFactory<TProgram>
    where TProgram : class
{
    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly string _modelKey;
    private readonly Action<IServiceCollection>? _services;
    private readonly ConcurrentQueue<LogEntry> _logs = new();

    public BeaconHost(string modelKey, IChatClient? model = null, IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null)
    {
        _modelKey = modelKey;
        _settings = settings ?? new Dictionary<string, string?>();
        _services = services;
        Model = model ?? TestModel.Create();
    }

    public IChatClient Model { get; }

    public RecordingChatClient Recorder => (RecordingChatClient)Model;

    public ManualClock Clock { get; } = new();

    public IReadOnlyList<LogEntry> Logs => [.. _logs];

    /// <summary>A client that sends <paramref name="token"/> on every request.</summary>
    public HttpClient CreateClient(string? token)
    {
        var http = CreateClient();
        if (token is not null)
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        return http;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in TestTokens.Settings.Concat(_settings))
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(new CapturingLoggerProvider(_logs)));
        builder.ConfigureTestServices(services =>
        {
            services.AddKeyedSingleton(_modelKey, Model);
            services.AddSingleton<TimeProvider>(Clock);
            _services?.Invoke(services);
        });
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<LogEntry> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, entries);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                entries.Enqueue(new LogEntry(category, logLevel, eventId.Id, formatter(state, exception)));
            }
        }
    }
}

/// <summary>The two hosts, with their model keys.</summary>
public static class Hosts
{
    public static BeaconHost<global::Ch23.Beacon.Agent.Program> Agent(
        IReadOnlyDictionary<string, string?>? settings = null, IChatClient? model = null, Action<IServiceCollection>? services = null) =>
        new(global::Ch23.Beacon.Agent.Program.ModelKey, model, settings, services);

    public static BeaconHost<global::Ch23.Beacon.AgentFramework.Program> AgentFramework(
        IReadOnlyDictionary<string, string?>? settings = null, IChatClient? model = null) =>
        new(global::Ch23.Beacon.AgentFramework.Program.ModelKey, model, settings);
}
