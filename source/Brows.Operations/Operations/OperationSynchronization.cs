using System;
using System.Threading;

namespace Brows.Operations;

internal sealed class OperationSynchronization {
    private readonly object Gate = new();
    private readonly CollectionChangeQueue CollectionChanges;

    public SynchronizationContext SynchronizationContext { get; }

    public OperationSynchronization(SynchronizationContext synchronizationContext) {
        SynchronizationContext = synchronizationContext;
        CollectionChanges = new CollectionChangeQueue(synchronizationContext);
    }

    public T Read<T>(Func<T> read) {
        lock (Gate) {
            return read();
        }
    }

    public void Update(Action update, Action collectionChange) {
        var schedule = false;
        try {
            lock (Gate) {
                update();
                schedule = CollectionChanges.Enqueue(collectionChange);
            }
        }
        finally {
            if (schedule) {
                CollectionChanges.Schedule();
            }
        }
    }

    public void Update(Action update, Action collectionChange, Action afterUpdate) {
        var schedule = false;
        try {
            lock (Gate) {
                update();
                schedule = CollectionChanges.Enqueue(collectionChange);
                afterUpdate?.Invoke();
            }
        }
        finally {
            if (schedule) {
                CollectionChanges.Schedule();
            }
        }
    }

    public void Update(Action update) {
        lock (Gate) {
            update();
        }
    }

    public T Update<T>(Func<T> update) {
        lock (Gate) {
            return update();
        }
    }
}
