// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Testing.Kubernetes;

public sealed class NativeWatchTestClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(timer => timer.Due <= _now).ToList();
            foreach (var timer in due)
                timer.Due = DateTimeOffset.MaxValue;
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(
        NativeWatchTestClock owner,
        TimerCallback callback,
        object? state
    ) : ITimer
    {
        public DateTimeOffset Due { get; set; } = DateTimeOffset.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime;
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                Due = DateTimeOffset.MaxValue;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Fire() => callback(state);
    }
}
