using System.Collections.Concurrent;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The tests' clock (house rule 7, owner 2026-11-03: "tests must not rely on the system clock"). Its
/// time moves only when told, and so do its timers. Setting <see cref="Now"/> moves the time a reader
/// sees and fires nothing, for code that only reads the time, so an app's background loops stay still
/// while a test moves its days; <see cref="Advance"/> moves it and fires every timer then due, as
/// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>, <see cref="PeriodicTimer"/>, a
/// <see cref="CancellationTokenSource"/> or a WaitAsync on this clock set them. Its timestamps follow
/// its time forward, so a duration measured on it is the time moved, never the machine's.
/// </summary>
/// <remarks>
/// Shared by an app's threads and the test's, without a lock (house rule 4): the time is one immutable
/// <see cref="Moment"/> swapped whole, each timer's setting likewise, and the timers a concurrent set.
/// </remarks>
internal sealed class ManualClock : TimeProvider
{
    private readonly ConcurrentDictionary<ManualTimer, byte> _timers = new();
    private Moment _moment;
    private int _fired;

    public ManualClock(DateTimeOffset start) => this._moment = new Moment(start, 0);

    /// <summary>Raised whenever the time moves or a timer is set, fired or put away, for a test to wait on.</summary>
    public Pulse Changed { get; } = new();

    /// <summary>The time readers see. Setting it fires no timer.</summary>
    public DateTimeOffset Now
    {
        get => Volatile.Read(ref this._moment).At;
        set
        {
            this.Move(_ => value);
            this.Changed.Raise();
        }
    }

    /// <summary>How many times a timer has fired: for a check that moving the clock set nothing off.</summary>
    public int Fired => Volatile.Read(ref this._fired);

    /// <summary>How many timers are set and not yet fired or put away.</summary>
    public int Waiting => this._timers.Keys.Count(t => t.Due is not null);

    /// <summary>How many timers set for <paramref name="dueTime"/> are waiting: one wait told apart from the app's others.</summary>
    public int WaitingFor(TimeSpan dueTime) => this._timers.Keys.Count(t => t.Due is not null && t.DueTime == dueTime);

    public override DateTimeOffset GetUtcNow() => this.Now;

    public override long GetTimestamp() => Volatile.Read(ref this._moment).Stamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        this._timers.TryAdd(timer, 0);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the time on by <paramref name="by"/> and fires, on this thread, every timer then due.</summary>
    public void Advance(TimeSpan by)
    {
        Moment moved = this.Move(at => at + by);
        foreach (ManualTimer timer in this._timers.Keys)
        {
            if (timer.TryClaim(moved.At))
            {
                Interlocked.Increment(ref this._fired);
                timer.Fire();
            }
        }
        this.Changed.Raise();
    }

    private Moment Move(Func<DateTimeOffset, DateTimeOffset> to)
    {
        while (true)
        {
            Moment seen = Volatile.Read(ref this._moment);
            DateTimeOffset at = to(seen.At);
            Moment next = new(at, seen.Stamp + Math.Max(0, (at - seen.At).Ticks));
            if (ReferenceEquals(Interlocked.CompareExchange(ref this._moment, next, seen), seen))
            {
                return next;
            }
        }
    }

    /// <summary>The time, and a timestamp that only ever moves forward with it.</summary>
    private sealed record Moment(DateTimeOffset At, long Stamp);

    /// <summary>When a timer is due (null when not set), the wait it was set for, and its period, if any.</summary>
    private sealed record Setting(DateTimeOffset? Due, TimeSpan DueTime, TimeSpan? Period);

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private Setting _setting = new(null, Timeout.InfiniteTimeSpan, null);
        private int _disposed;

        public DateTimeOffset? Due => Volatile.Read(ref this._setting).Due;

        public TimeSpan DueTime => Volatile.Read(ref this._setting).DueTime;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (Volatile.Read(ref this._disposed) != 0)
            {
                return false;
            }
            DateTimeOffset now = clock.Now;
            Volatile.Write(ref this._setting, new Setting(
                dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime,
                dueTime,
                period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period));
            clock.Changed.Raise();
            return true;
        }

        /// <summary>Takes the firing due by <paramref name="now"/>, once: a period sets the next, otherwise none.</summary>
        public bool TryClaim(DateTimeOffset now)
        {
            while (true)
            {
                Setting seen = Volatile.Read(ref this._setting);
                if (seen.Due is not { } due || due > now)
                {
                    return false;
                }
                Setting next = seen with { Due = seen.Period is { } period ? now + period : null };
                if (ReferenceEquals(Interlocked.CompareExchange(ref this._setting, next, seen), seen))
                {
                    return true;
                }
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            Volatile.Write(ref this._disposed, 1);
            clock._timers.TryRemove(this, out _);
            clock.Changed.Raise();
        }

        public ValueTask DisposeAsync()
        {
            this.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A signal raised each time something changes, so a test can wait for what it expects without
/// polling on the clock: take <see cref="Next"/>, look, and if it is not there yet, wait on it.
/// </summary>
internal sealed class Pulse
{
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes at the next <see cref="Raise"/> after it was taken.</summary>
    public Task Next => Volatile.Read(ref this._next).Task;

    public void Raise() =>
        Interlocked.Exchange(ref this._next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}

/// <summary>Waits on signals, never on time.</summary>
internal static class Eventually
{
    /// <summary>
    /// How long a wait may last before it fails. A guard against a hang, never what a test waits for:
    /// a passing test returns at the signal, however soon.
    /// </summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns once <paramref name="done"/> holds: checked now, and again each time one of
    /// <paramref name="changes"/> is raised. Fails, saying what never came, after <see cref="Patience"/>.
    /// </summary>
    public static async Task Until(Func<bool> done, string what, params Pulse[] changes)
    {
        while (true)
        {
            Task changed = Task.WhenAny(changes.Select(c => c.Next));
            if (done())
            {
                return;
            }
            try
            {
                await changed.WaitAsync(Patience);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Never: {what}.");
            }
        }
    }
}

/// <summary>What never answers until it is cancelled: storage or a server that hangs.</summary>
internal static class Never
{
    public static Task Until(CancellationToken token) => new TaskCompletionSource().Task.WaitAsync(token);
}
