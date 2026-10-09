using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ch25.Beacon.Agent.Telemetry;

/// <summary>
/// A logger factory that holds the categories under a prefix to two rules, whatever the logging
/// configuration says: nothing below a minimum level, and no exception message. A filter rule can't promise
/// the first: the logging system picks the most specific rule, so a provider's own rule or a longer category
/// in configuration beats <c>AddFilter</c>.
/// </summary>
public sealed class CappedLoggerFactory(ILoggerFactory inner, string categoryPrefix, LogLevel minimumLevel) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName)
    {
        var logger = inner.CreateLogger(categoryName);
        return categoryName.StartsWith(categoryPrefix, StringComparison.Ordinal) ? new CappedLogger(logger, minimumLevel) : logger;
    }

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose() => inner.Dispose();

    private sealed class CappedLogger(ILogger inner, LogLevel minimumLevel) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= minimumLevel)
            {
                // A failed model or tool call is logged with its exception, whose message can quote content.
                inner.Log(logLevel, eventId, state, exception is null ? null : new RedactedException(exception), formatter);
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
    }
}

/// <summary>Stands in for a logged exception: its type, without its message or stack trace.</summary>
public sealed class RedactedException(Exception original) : Exception($"{original.GetType().FullName} (message redacted)");

public static class LogLevelCapExtensions
{
    /// <summary>
    /// Puts a <see cref="CappedLoggerFactory"/> in front of the app's logger factory, which keeps its
    /// providers, configuration and filters. Every <see cref="ILogger"/> the app resolves, including the
    /// ones the chat pipeline's layers create, comes through the cap.
    /// </summary>
    public static IServiceCollection CapLogLevel(this IServiceCollection services, string categoryPrefix, LogLevel minimumLevel)
    {
        services.TryAddSingleton<LoggerFactory>();
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(sp =>
            new CappedLoggerFactory(sp.GetRequiredService<LoggerFactory>(), categoryPrefix, minimumLevel)));
        return services;
    }
}
