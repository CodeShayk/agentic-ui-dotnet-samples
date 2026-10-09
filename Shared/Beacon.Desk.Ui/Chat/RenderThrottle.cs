namespace Beacon.Desk.Ui;

/// <summary>
/// Limits how often a streaming component re-renders. A request after a quiet period renders at once;
/// requests that arrive within <see cref="Interval"/> of the last render are folded into one trailing
/// render, so the latest text still appears when the stream pauses (for example while a tool runs).
/// </summary>
/// <remarks>
/// The trailing render fires on a timer thread, so <c>render</c> must marshal to the component's
/// dispatcher. Pass <c>() =&gt; InvokeAsync(StateHasChanged)</c>.
/// </remarks>
public sealed class RenderThrottle : IDisposable
{
    private readonly Func<Task> _render;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private long _lastRender;
    private bool _hasRendered;
    private ITimer? _trailing;
    private bool _disposed;

    public RenderThrottle(Func<Task> render, TimeSpan interval, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);

        _render = render;
        Interval = interval;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The minimum time between two renders.</summary>
    public TimeSpan Interval { get; }

    /// <summary>
    /// Asks for a render: now, if <see cref="Interval"/> has passed since the last one; otherwise once,
    /// when it has. Call it after applying each streamed update.
    /// </summary>
    public Task RequestAsync()
    {
        lock (_gate)
        {
            if (_disposed || _trailing is not null)
            {
                // A trailing render is already scheduled, and it will show this change too.
                return Task.CompletedTask;
            }

            var elapsed = _hasRendered ? _time.GetElapsedTime(_lastRender) : TimeSpan.MaxValue;
            if (elapsed < Interval)
            {
                _trailing = _time.CreateTimer(_ => RenderTrailing(), null, Interval - elapsed, Timeout.InfiniteTimeSpan);
                return Task.CompletedTask;
            }

            MarkRendered();
        }

        return _render();
    }

    /// <summary>Renders now and cancels any pending trailing render. Call it when the stream ends.</summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            CancelTrailing();
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            MarkRendered();
        }

        return _render();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CancelTrailing();
        }
    }

    private void RenderTrailing()
    {
        lock (_gate)
        {
            if (_disposed || _trailing is null)
            {
                return; // flushed or disposed in the meantime
            }

            CancelTrailing();
            MarkRendered();
        }

        _ = RenderSafelyAsync();
    }

    private async Task RenderSafelyAsync()
    {
        try
        {
            await _render();
        }
        catch (ObjectDisposedException)
        {
            // The component (or its circuit) went away between scheduling and firing.
        }
    }

    private void MarkRendered()
    {
        _lastRender = _time.GetTimestamp();
        _hasRendered = true;
    }

    private void CancelTrailing()
    {
        _trailing?.Dispose();
        _trailing = null;
    }
}
