using Beacon.Desk.Ui;

namespace Ch06.Tests;

/// <summary>RenderThrottle on a clock the test controls.</summary>
public sealed class RenderThrottleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task A_burst_of_updates_renders_once_now_and_once_when_the_interval_ends()
    {
        var clock = new ManualTimeProvider();
        var renders = 0;
        using var throttle = new RenderThrottle(() => { renders++; return Task.CompletedTask; }, Interval, clock);

        await throttle.RequestAsync(); // after a quiet period: at once
        Assert.Equal(1, renders);

        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(4));
            await throttle.RequestAsync(); // inside the interval: folded into one trailing render
        }

        Assert.Equal(1, renders);

        clock.Advance(TimeSpan.FromMilliseconds(10)); // 50 ms after the first render
        Assert.Equal(2, renders);
    }

    [Fact]
    public async Task Flush_renders_now_and_cancels_the_pending_render()
    {
        var clock = new ManualTimeProvider();
        var renders = 0;
        using var throttle = new RenderThrottle(() => { renders++; return Task.CompletedTask; }, Interval, clock);

        await throttle.RequestAsync();
        await throttle.RequestAsync(); // schedules a trailing render
        await throttle.FlushAsync();   // the stream ended: render the final state now
        Assert.Equal(2, renders);

        clock.Advance(Interval);
        Assert.Equal(2, renders);
    }

    [Fact]
    public async Task Nothing_renders_after_dispose()
    {
        var clock = new ManualTimeProvider();
        var renders = 0;
        var throttle = new RenderThrottle(() => { renders++; return Task.CompletedTask; }, Interval, clock);

        await throttle.RequestAsync();
        await throttle.RequestAsync();
        throttle.Dispose();
        clock.Advance(Interval);
        await throttle.FlushAsync();

        Assert.Equal(1, renders);
    }

    /// <summary>A clock that moves only when told to, and fires due timers synchronously.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _now + dueTime.Ticks);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by.Ticks;
            foreach (var timer in _timers.Where(t => !t.Disposed && t.Due <= _now).ToList())
            {
                timer.Disposed = true;
                timer.Callback(timer.State);
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, long due) : ITimer
        {
            public TimerCallback Callback { get; } = callback;

            public object? State { get; } = state;

            public long Due { get; } = due;

            public bool Disposed { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
