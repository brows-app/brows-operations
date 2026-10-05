using System;
using System.Collections.Generic;
using System.Threading;

namespace Brows.Collections;

internal sealed class CollectionChangeQueue {
    private readonly Queue<Action> Changes = new();
    private readonly SynchronizationContext SynchronizationContext;
    private readonly
#if NET9_0_OR_GREATER
        Lock
#else
        object
#endif
        Gate = new();

    private bool Scheduled;

    private void Drain() {
        try {
            while (true) {
                Action change;
                lock (Gate) {
                    if (Changes.Count == 0) {
                        return;
                    }
                    change = Changes.Dequeue();
                }
                change();
            }
        }
        finally {
            var schedule = false;
            lock (Gate) {
                Scheduled = false;
                if (Changes.Count > 0) {
                    Scheduled = true;
                    schedule = true;
                }
            }
            if (schedule) {
                Schedule();
            }
        }
    }

    public CollectionChangeQueue(SynchronizationContext synchronizationContext) {
        SynchronizationContext = synchronizationContext;
    }

    public void Schedule() {
        if (SynchronizationContext is null) {
            Drain();
            return;
        }
        try {
            SynchronizationContext.Post(_ => Drain(), null);
        }
        catch {
            lock (Gate) {
                Scheduled = false;
            }
            throw;
        }
    }

    public bool Enqueue(Action change) {
        if (change is null) throw new ArgumentNullException(nameof(change));
        lock (Gate) {
            Changes.Enqueue(change);
            if (Scheduled != true) {
                Scheduled = true;
                return true;
            }
        }
        return false;
    }
}
