using System.Collections.Concurrent;
using System.Diagnostics;
using Ch27.Beacon.Agent.Security.History;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace Ch27.Tests;

/// <summary>One entry a host logged, after redaction, with its exception as a provider would print it.</summary>
public sealed record LogEntry(string Category, LogLevel Level, int EventId, string Message, string? Exception);

/// <summary>
/// Hosts Ch27.Beacon.Agent exactly as its <c>Program.Main</c> builds it, on the scripted model with no streaming
/// delay unless a test asks for one, with the development token settings, a conversation store in a fresh folder
/// (or a given one, to restart over it), captured logs and spans. <c>kestrel: true</c> puts it on a real port.
/// </summary>
public sealed class AgentHost : WebApplicationFactory<global::Ch27.Beacon.Agent.Program>
{
    private readonly (string Key, string? Value)[] _settings;
    private readonly string _environment;
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private readonly List<Activity> _spans = [];

    public AgentHost(string? conversations = null, string environment = "Testing", params (string Key, string? Value)[] settings)
    {
        Conversations = conversations ?? Path.Combine(Path.GetTempPath(), "ch27-tests", Guid.NewGuid().ToString("N"));
        _environment = environment;
        _settings = settings;
    }

    /// <summary>The folder the conversation store writes to.</summary>
    public string Conversations { get; }

    public IReadOnlyList<LogEntry> Logs => [.. _logs];

    public IReadOnlyList<Activity> Spans
    {
        get
        {
            lock (_spans)
            {
                return [.. _spans];
            }
        }
    }

    public IConversationStore Store => Services.GetRequiredService<IConversationStore>();

    /// <summary>Where the agent listens: Kestrel's real port once started with <see cref="StartOnKestrel"/>.</summary>
    public Uri Address => CreateClient().BaseAddress!;

    /// <summary>A host in memory with extra settings on top.</summary>
    public static AgentHost With(params (string Key, string? Value)[] settings) => new(null, "Testing", settings);

    public static AgentHost StartOnKestrel(string? conversations = null, params (string Key, string? Value)[] settings)
    {
        var host = new AgentHost(conversations, "Testing", settings);
        host.UseKestrel(0);
        host.StartServer();
        return host;
    }

    /// <summary>A client that sends <paramref name="token"/> on every request; on Kestrel, a real socket.</summary>
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
        builder.UseEnvironment(_environment);
        builder.UseSetting("AI:Provider", "Scripted");
        builder.UseSetting("AI:ScriptedChunkDelayMilliseconds", "0");
        builder.UseSetting("Beacon:Conversations:Directory", Conversations);
        foreach (var (key, value) in DevTokens.AgentSettings)
        {
            builder.UseSetting(key, value);
        }

        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(_logs)));
        builder.ConfigureTestServices(services =>
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(new LockedList(_spans))));
    }

    /// <summary>The in-memory exporter adds from whichever thread ends a span.</summary>
    private sealed class LockedList(List<Activity> items) : ICollection<Activity>
    {
        public int Count { get { lock (items) { return items.Count; } } }

        public bool IsReadOnly => false;

        public void Add(Activity item) { lock (items) { items.Add(item); } }

        public void Clear() { lock (items) { items.Clear(); } }

        public bool Contains(Activity item) { lock (items) { return items.Contains(item); } }

        public void CopyTo(Activity[] array, int arrayIndex) { lock (items) { items.CopyTo(array, arrayIndex); } }

        public bool Remove(Activity item) { lock (items) { return items.Remove(item); } }

        public IEnumerator<Activity> GetEnumerator() { lock (items) { return items.ToList().GetEnumerator(); } }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Keeps every entry the app's filters (and Chapter 25's cap) let through, after redaction.</summary>
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
                entries.Enqueue(new LogEntry(category, logLevel, eventId.Id, formatter(state, exception), exception?.ToString()));
        }
    }
}

/// <summary>
/// Hosts Ch27.Beacon.Desk as its <c>Program.Main</c> builds it, in Development (so it signs in with the
/// development sign-in and signs agent tokens with the test key), calling the agent at <paramref name="agent"/>.
/// </summary>
public sealed class DeskHost(Uri agent, string environment = "Development", params (string Key, string? Value)[] settings)
    : WebApplicationFactory<global::Ch27.Beacon.Desk.Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("BeaconAgent:DevelopmentTokens:SigningKey", DevTokens.Key);
        builder.UseSetting("Services:beacon-agent:http:0", agent.GetLeftPart(UriPartial.Authority));
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }

    /// <summary>
    /// The Desk's chat client as one circuit sees it, with that circuit's user signed in, the way Blazor sets the
    /// authentication state when a circuit starts. Dispose the scope to end the "circuit".
    /// </summary>
    public (AsyncServiceScope Circuit, IChatClient Chat) OpenCircuit(System.Security.Claims.ClaimsPrincipal user)
    {
        var circuit = Services.CreateAsyncScope();
        var authentication = (IHostEnvironmentAuthenticationStateProvider)circuit.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        authentication.SetAuthenticationState(Task.FromResult(new AuthenticationState(user)));
        return (circuit, circuit.ServiceProvider.GetRequiredService<IChatClient>());
    }
}
