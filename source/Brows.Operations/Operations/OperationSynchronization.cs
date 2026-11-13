using Brows.Collections;
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Brows.Operations;

internal sealed class OperationSynchronization {
    [ThreadStatic]
    private static Queue<(Action Callback, bool Required)> Notifications;

    private readonly CollectionChangeQueue CollectionChanges;
    private readonly
#if NET9_0_OR_GREATER
        Lock
#else
        object
#endif
        Gate = new();

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
            catch (Exception ex) {
                /*
                 * A listener failure must not leave committed collection changes without a scheduled drain.
                 */
                var failure = ExceptionDispatchInfo.Capture(ex);
                try {
                    DrainNotifications(notifications, requiredOnly: true);
                }
                catch (Exception requiredFailure) {
                    OperationInfrastructureAggregateException.Combine(
                        failure,
                        ExceptionDispatchInfo.Capture(requiredFailure)).Throw();
                }
                failure.Throw();
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
        var result = default(T);
        ExceptionDispatchInfo failure = null;
        try {
            lock (Gate) {
                result = update();
            }
        }
        catch (Exception ex) {
            failure = ExceptionDispatchInfo.Capture(ex);
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
                catch (Exception notificationFailure) {
                    var captured = ExceptionDispatchInfo.Capture(notificationFailure);
                    failure = failure is null
                        ? captured
                        : OperationInfrastructureAggregateException.Combine(failure, captured);
                }
                finally {
                    Notifications = null;
                }
            }
        }
        failure?.Throw();
        return result;
    }

    public void Notify(Action notification) {
        EnqueueNotification(notification);
    }
}
