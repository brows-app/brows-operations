using System;
using System.Collections.Generic;
using System.Threading;

namespace Brows.Operations;

internal sealed class OperationSynchronization {
    [ThreadStatic]
    private static Queue<(Action Callback, bool Required)> Notifications;

    private readonly object Gate = new();
    private readonly CollectionChangeQueue CollectionChanges;

    private static void DrainNotifications(Queue<(Action Callback, bool Required)> notifications,
                                           bool requiredOnly = false) {
        while (notifications.Count > 0) {
            var notification = notifications.Dequeue();
            if (requiredOnly && !notification.Required) {
                continue;
            }
            try {
                notification.Callback();
            }
            catch {
                /*
                 * A listener failure must not leave committed collection changes without a scheduled drain.
                 */
                DrainNotifications(notifications, requiredOnly: true);
                throw;
            }
        }
    }

    private static void EnqueueNotification(Action notification, bool required = false) {
        if (Notifications is null) {
            notification();
        }
        else {
            Notifications.Enqueue((notification, required));
        }
    }

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
        Update(update, collectionChange, null);
    }

    public void Update(Action update, Action collectionChange, Action afterUpdate) {
        Update(() => {
            update();
            var schedule = CollectionChanges.Enqueue(collectionChange);
            try {
                afterUpdate?.Invoke();
            }
            finally {
                if (schedule) {
                    EnqueueNotification(CollectionChanges.Schedule, required: true);
                }
            }
        });
    }

    public void Update(Action update) {
        Update(() => { update(); return true; });
    }

    public T Update<T>(Func<T> update) {
        var outermost = Notifications is null;
        var notifications = Notifications ??= new();
        try {
            lock (Gate) {
                return update();
            }
        }
        finally {
            if (outermost) {
                try {
                    /*
                     * Nested updates and reentrant reports share this thread's batch. Publish only after
                     * every state gate acquired by the outer update has been released.
                     */
                    DrainNotifications(notifications);
                }
                finally {
                    Notifications = null;
                }
            }
        }
    }

    public void Notify(Action notification) {
        EnqueueNotification(notification);
    }
}
