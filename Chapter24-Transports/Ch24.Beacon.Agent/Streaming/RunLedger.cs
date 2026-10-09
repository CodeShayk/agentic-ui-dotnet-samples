using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Ch24.Beacon.Agent.Streaming;

/// <summary>
/// Remembers which run IDs this server has accepted, for <see cref="StreamingOptions.RunIdRetention"/>.
/// A client that retries a request it isn't sure arrived resends the same run ID; if the first copy
/// did arrive, the retry is refused instead of running the agent, and its tools, a second time.
/// </summary>
/// <remarks>
/// This ledger lives in one process. Behind a load balancer with several instances, keep it in a
/// shared store with an atomic "add if absent" and an expiry (a Redis <c>SET NX PX</c>, for example).
/// </remarks>
public sealed class RunLedger(TimeProvider time, IOptions<StreamingOptions> options)
{
    private readonly ConcurrentDictionary<(string ThreadId, string RunId), DateTimeOffset> _accepted = new();
    private long _nextSweepTicks;

    /// <summary>How many run IDs are currently remembered.</summary>
    public int Count => _accepted.Count;

    /// <summary>Records the run and returns <see langword="true"/>, or <see langword="false"/> if it was already recorded.</summary>
    public bool TryAccept(string threadId, string runId)
    {
        var now = time.GetUtcNow();
        SweepExpired(now);
        return _accepted.TryAdd((threadId, runId), now + options.Value.RunIdRetention);
    }

    private void SweepExpired(DateTimeOffset now)
    {
        // At most one sweep a minute, by whichever request gets there first.
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < due
            || Interlocked.CompareExchange(ref _nextSweepTicks, now.AddMinutes(1).UtcTicks, due) != due)
        {
            return;
        }

        foreach (var (key, expires) in _accepted)
        {
            if (expires <= now)
            {
                _accepted.TryRemove(key, out _);
            }
        }
    }
}
