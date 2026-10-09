using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Ch05.Tests;

/// <summary>The chapter's two ways of hosting Beacon's agent.</summary>
public enum HostKind
{
    /// <summary>Ch05.Beacon.Agent: the Microsoft.Extensions.AI pipeline behind the book's endpoint glue.</summary>
    Plain,

    /// <summary>Ch05.Beacon.AgentFramework: the same pipeline as an Agent Framework agent behind MapAGUIServer.</summary>
    AgentFramework,
}

/// <summary>One entry the host logged.</summary>
public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>An agent hosted in memory.</summary>
public interface IAgentHost : IAsyncDisposable
{
    HttpClient CreateClient();

    /// <summary>Everything the host logged at Information or above.</summary>
    IReadOnlyList<LogEntry> Logs { get; }
}

/// <summary>
/// Hosts one of the chapter's agents in memory, exactly as its <c>Program.Main</c> builds it, with
/// configuration overrides layered on top. The baseline is the scripted model with no streaming delay.
/// </summary>
public sealed class AgentHost<TEntryPoint>(IReadOnlyDictionary<string, string?> settings)
    : WebApplicationFactory<TEntryPoint>, IAgentHost
    where TEntryPoint : class
{
    private readonly CapturingLoggerProvider _logs = new();

    public IReadOnlyList<LogEntry> Logs => _logs.Entries;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: no User Secrets, no debug-level model logging in test output.
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.AddProvider(_logs));

        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }
}

public static class AgentHosts
{
    /// <summary>Configuration every test starts from: Beacon's scripted model, streaming without pauses.</summary>
    public static IReadOnlyDictionary<string, string?> Scripted { get; } = new Dictionary<string, string?>
    {
        ["AI:Provider"] = "Scripted",
        ["AI:ScriptedChunkDelayMilliseconds"] = "0",
    };

    /// <summary>Configuration that points the agent at an Ollama server (here, a fake one).</summary>
    public static IReadOnlyDictionary<string, string?> Ollama(Uri endpoint) => new Dictionary<string, string?>
    {
        ["AI:Provider"] = "Ollama",
        ["AI:OllamaEndpoint"] = endpoint.ToString(),
        ["AI:OllamaModel"] = "llama3.2",
    };

    public static IAgentHost Start(HostKind kind, IReadOnlyDictionary<string, string?>? settings = null, params (string Key, string? Value)[] extra)
    {
        var merged = new Dictionary<string, string?>(settings ?? Scripted);
        foreach (var (key, value) in extra)
        {
            merged[key] = value;
        }

        return kind switch
        {
            HostKind.Plain => new AgentHost<global::Ch05.Beacon.Agent.Program>(merged),
            HostKind.AgentFramework => new AgentHost<global::Ch05.Beacon.AgentFramework.Program>(merged),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
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
