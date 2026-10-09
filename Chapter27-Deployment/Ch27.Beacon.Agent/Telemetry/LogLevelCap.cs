// Copied from Chapter 25 (Ch25.Beacon.Agent). Chapter 27 composes the cap with whatever ILoggerFactory is already
// registered: Chapter 23's redaction (EnableRedaction) replaces the factory too, and wrapping a fresh LoggerFactory
// would silently drop it.
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ch27.Beacon.Agent.Telemetry;

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
    /// Puts a <see cref="CappedLoggerFactory"/> in front of the logger factory registered so far, which keeps its
    /// providers, configuration, filters and redaction. Every <see cref="ILogger"/> the app resolves, including the
    /// ones the chat pipeline's layers create, comes through the cap. Call it after anything else that replaces
    /// the factory (Chapter 23's <c>AddBeaconRedaction</c>).
    /// </summary>
    public static IServiceCollection CapLogLevel(this IServiceCollection services, string categoryPrefix, LogLevel minimumLevel)
    {
        var current = services.LastOrDefault(d => d.ServiceType == typeof(ILoggerFactory) && !d.IsKeyedService)
            ?? ServiceDescriptor.Singleton<ILoggerFactory, LoggerFactory>();
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(sp =>
            new CappedLoggerFactory(Create(sp, current), categoryPrefix, minimumLevel)));
        return services;
    }

    // Builds the factory the descriptor describes, as the container would have.
    private static ILoggerFactory Create(IServiceProvider services, ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance as ILoggerFactory
        ?? descriptor.ImplementationFactory?.Invoke(services) as ILoggerFactory
        ?? (ILoggerFactory)ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!);
}
