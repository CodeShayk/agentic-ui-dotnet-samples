using AGUI.Abstractions;
using Microsoft.Extensions.Options;

namespace Ch27.Beacon.Agent.Hosting;

/// <summary>
/// Bounds every run in time and lets a replica shut down without cutting streams mid-sentence.
/// Each run gets a budget; when shutdown begins the replica stops taking runs, gives the ones in
/// flight <see cref="RunLimitsOptions.DrainSeconds"/> and then stops them with <c>RUN_ERROR</c>.
/// </summary>
public sealed partial class RunGovernor : IDisposable
{
    public const string RunTimeoutCode = "RUN_TIMEOUT";
    public const string ServerShutdownCode = "SERVER_SHUTDOWN";

    private readonly RunLimitsOptions _limits;
    private readonly ILogger<RunGovernor> _logger;
    private readonly CancellationTokenSource _drainDeadline = new();
    private int _activeRuns;
    private int _draining;

    public RunGovernor(IOptions<RunLimitsOptions> limits, IHostApplicationLifetime lifetime, ILogger<RunGovernor> logger)
    {
        _limits = limits.Value;
        _logger = logger;

        // SIGTERM from the platform (scale-in, a new revision) arrives here first.
        lifetime.ApplicationStopping.Register(BeginDrain);
    }

    /// <summary>True once shutdown has begun: readiness fails and new runs are refused with 503.</summary>
    public bool IsDraining => Volatile.Read(ref _draining) == 1;

    public int ActiveRuns => Volatile.Read(ref _activeRuns);

    public TimeSpan RunBudget => TimeSpan.FromSeconds(_limits.RunBudgetSeconds);

    public void BeginDrain()
    {
        if (Interlocked.Exchange(ref _draining, 1) == 1)
        {
            return;
        }

        LogDraining(_logger, ActiveRuns, _limits.DrainSeconds);
        _drainDeadline.CancelAfter(TimeSpan.FromSeconds(_limits.DrainSeconds));
    }

    /// <summary>Starts a run. Its token fires when the request ends, the budget runs out or the drain deadline passes.</summary>
    public RunScope BeginRun(CancellationToken requestAborted)
    {
        Interlocked.Increment(ref _activeRuns);
        return new RunScope(this, requestAborted);
    }

    public void Dispose() => _drainDeadline.Dispose();

    [LoggerMessage(EventId = 2701, Level = LogLevel.Information,
        Message = "Shutdown began: {ActiveRuns} run(s) in flight get {DrainSeconds} s to finish; new runs get 503.")]
    private static partial void LogDraining(ILogger logger, int activeRuns, double drainSeconds);

    /// <summary>One run's view of the limits.</summary>
    public sealed class RunScope : IDisposable
    {
        private readonly RunGovernor _governor;
        private readonly CancellationTokenSource _budget;
        private readonly CancellationTokenSource _linked;

        internal RunScope(RunGovernor governor, CancellationToken requestAborted)
        {
            _governor = governor;
            _budget = new CancellationTokenSource(governor.RunBudget);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _budget.Token, governor._drainDeadline.Token);
        }

        /// <summary>The token to pass to the model and its tools.</summary>
        public CancellationToken Token => _linked.Token;

        /// <summary>Why the server stopped this run, or null if it didn't (the client may have gone).</summary>
        public string? StopCode =>
            _governor._drainDeadline.IsCancellationRequested ? ServerShutdownCode
            : _budget.IsCancellationRequested ? RunTimeoutCode
            : null;

        /// <summary>The terminal event that tells a client still listening why its run ended.</summary>
        public RunErrorEvent CreateError() => StopCode == ServerShutdownCode
            ? new RunErrorEvent { Code = ServerShutdownCode, Message = "Beacon is restarting and stopped this run. Send your message again." }
            : new RunErrorEvent { Code = RunTimeoutCode, Message = $"The run took longer than {_governor.RunBudget.TotalSeconds:0} seconds and was stopped." };

        public void Dispose()
        {
            Interlocked.Decrement(ref _governor._activeRuns);
            _linked.Dispose();
            _budget.Dispose();
        }
    }
}
