using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Brows.Operations;

internal sealed class ControlledSynchronizationContext : SynchronizationContext {
    private readonly object Gate = new();
    private readonly List<(SendOrPostCallback Callback, object State)> Callbacks = [];

    private bool RunAt(bool newest) {
        (SendOrPostCallback Callback, object State) work;
        lock (Gate) {
            var index = newest ? Callbacks.Count - 1 : 0;
            if (index < 0 || index >= Callbacks.Count) {
                return false;
            }
            work = Callbacks[index];
            Callbacks.RemoveAt(index);
        }

        var previous = Current;
        SetSynchronizationContext(this);
        try {
            work.Callback(work.State);
        }
        finally {
            SetSynchronizationContext(previous);
        }
        return true;
    }

    public override void Post(SendOrPostCallback callback, object state) {
        lock (Gate) {
            Callbacks.Add((callback, state));
            Monitor.PulseAll(Gate);
        }
    }

    public bool WaitForCount(int count, TimeSpan timeout) {
        var timer = Stopwatch.StartNew();
        lock (Gate) {
            while (Callbacks.Count < count) {
                var remaining = timeout - timer.Elapsed;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(Gate, remaining)) {
                    return false;
                }
            }
            return true;
        }
    }

    public bool RunOldest() =>
        RunAt(newest: false);

    public bool RunNewest() =>
        RunAt(newest: true);

    public bool RunUntil(Func<bool> condition, bool newest, TimeSpan timeout) {
        var timer = Stopwatch.StartNew();
        while (!condition()) {
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero || !WaitForCount(1, remaining)) {
                return condition();
            }
            RunAt(newest);
        }
        return true;
    }
}
