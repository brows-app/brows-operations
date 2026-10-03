using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Brows.Operations;

internal sealed class OperationContext {
    [ThreadStatic]
    private static OperationContext Draining;

    private readonly
#if NET9_0_OR_GREATER
        Lock
#else
        object
#endif
        Gate = new();

    private readonly
#if NET9_0_OR_GREATER
        Lock
#else
        object
#endif
        QueueGate = new();

    private readonly Queue<Action> Work = new();
    private readonly Dictionary<object, ProgressReport> PendingReports = [];
    private readonly int OwnerThreadId;
    private bool Scheduled;
    private int CancellationDepth;
    private List<CancellationTokenSource> DeferredDisposals;

    private void Enqueue(Action work) {
        var post = false;
        lock (QueueGate) {
            Work.Enqueue(work);
            if (Scheduled != true) {
                Scheduled = true;
                post = true;
            }
        }
        if (post) {
            Post();
        }
    }

    private void Post() {
        try {
            SynchronizationContext.Post(_ => Run(null), null);
        }
        catch {
            lock (QueueGate) {
                Scheduled = false;
            }
            throw;
        }
    }

    private void Run(Action action) {
        lock (Gate) {
            var previous = Draining;
            Draining = this;
            try {
                if (previous != this) {
                    drainQueue();
                }
                action?.Invoke();
            }
            finally {
                Draining = previous;
            }
        }
        void drainQueue() {
            while (true) {
                Action work;
                lock (QueueGate) {
                    if (Work.Count == 0) {
                        Scheduled = false;
                        return;
                    }
                    work = Work.Dequeue();
                }
                try {
                    work();
                }
                catch {
                    /*
                     * Surface the failure on this context without stranding work queued behind it.
                     */
                    var post = false;
                    lock (QueueGate) {
                        Scheduled = Work.Count > 0 && SynchronizationContext is not null;
                        post = Scheduled;
                    }
                    if (post) {
                        Post();
                    }
                    throw;
                }
            }
        }
    }

    public SynchronizationContext SynchronizationContext { get; }

    public bool IsCurrent =>
        SynchronizationContext is null ||
        Draining == this ||
        Thread.CurrentThread.ManagedThreadId == OwnerThreadId;

    public OperationContext(SynchronizationContext synchronizationContext) {
        SynchronizationContext = synchronizationContext;
        OwnerThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    public bool IsOwnedBy(Thread thread) =>
        SynchronizationContext is not null &&
        thread is not null &&
        thread.ManagedThreadId == OwnerThreadId;

    public void Invoke(Action action) {
        if (action is null) throw new ArgumentNullException(nameof(action));
        if (IsCurrent) {
            Run(action);
        }
        else {
            Enqueue(action);
        }
    }

    public Task InvokeAsync(Action action) {
        if (action is null) throw new ArgumentNullException(nameof(action));
        return InvokeAsync(() => {
            action();
            return true;
        });
    }

    public Task<T> InvokeAsync<T>(Func<T> func) {
        if (func is null) throw new ArgumentNullException(nameof(func));
        if (IsCurrent) {
            try {
                var result = default(T);
                Run(() => result = func());
                return Task.FromResult(result);
            }
            catch (Exception ex) {
                return Task.FromException<T>(ex);
            }
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() => {
            T result;
            try {
                result = func();
            }
            catch (Exception ex) {
                completion.TrySetException(ex);
                return;
            }
            completion.TrySetResult(result);
        });
        return completion.Task;
    }

    public T Send<T>(Func<T> func) {
        if (func is null) {
            throw new ArgumentNullException(nameof(func));
        }
        var result = default(T);
        if (IsCurrent) {
            Run(() => result = func());
            return result;
        }
        var error = default(ExceptionDispatchInfo);
        SynchronizationContext.Send(_ => {
            try {
                Run(() => result = func());
            }
            catch (Exception ex) {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        }, null);
        error?.Throw();
        return result;
    }

    public void Report(object key, ProgressReport report, Action<ProgressReport> apply) {
        if (key is null) {
            throw new ArgumentNullException(nameof(key));
        }
        if (report is null) {
            throw new ArgumentNullException(nameof(report));
        }
        if (apply is null) {
            throw new ArgumentNullException(nameof(apply));
        }
        if (IsCurrent) {
            Run(() => {
                var pending = takeReport(key);
                apply(pending is null ? report : pending.Merge(report));
            });
            return;
        }
        var enqueue = false;
        lock (QueueGate) {
            if (PendingReports.TryGetValue(key, out var pending)) {
                PendingReports[key] = pending.Merge(report);
            }
            else {
                PendingReports[key] = report;
                enqueue = true;
            }
        }
        if (enqueue) {
            Enqueue(() => {
                var pending = takeReport(key);
                if (pending is not null) {
                    apply(pending);
                }
            });
        }
        ProgressReport takeReport(object key) {
            lock (QueueGate) {
                if (PendingReports.TryGetValue(key, out var report)) {
                    PendingReports.Remove(key);
                    return report;
                }
                return null;
            }
        }
    }

    public void BeginCancellation() {
        CancellationDepth++;
    }

    public void EndCancellation() {
        CancellationDepth--;
        if (CancellationDepth == 0 && DeferredDisposals is not null) {
            var sources = DeferredDisposals;
            DeferredDisposals = null;
            foreach (var source in sources) {
                source.Dispose();
            }
        }
    }

    public void Dispose(CancellationTokenSource source) {
        if (source is null) {
            throw new ArgumentNullException(nameof(source));
        }
        if (CancellationDepth > 0) {
            /*
             * Completion can run inline from a cancellation callback; dispose after Cancel returns.
             */
            (DeferredDisposals ??= []).Add(source);
        }
        else {
            source.Dispose();
        }
    }
}
