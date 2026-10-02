using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Brows;

internal static class UiTestThread {
    private sealed class PumpSynchronizationContext : SynchronizationContext {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object State)> Queue = [];
        private int AsyncVoidCount;
        private int TestTaskCompleted;
        private Exception CallbackException;

        public override void Post(SendOrPostCallback callback, object state) {
            Queue.Add((callback, state));
        }

        public override void Send(SendOrPostCallback callback, object state) {
            if (SynchronizationContext.Current == this) {
                callback(state);
                return;
            }
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ => {
                try {
                    callback(state);
                    completed.SetResult(true);
                }
                catch (Exception exception) {
                    completed.SetException(exception);
                }
            }, null);
            completed.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }

        public override void OperationStarted() {
            Interlocked.Increment(ref AsyncVoidCount);
        }

        public override void OperationCompleted() {
            Interlocked.Decrement(ref AsyncVoidCount);
        }

        public void TestCompleted() {
            Volatile.Write(ref TestTaskCompleted, 1);
        }

        public void Run() {
            var elapsed = Stopwatch.StartNew();
            while (Volatile.Read(ref TestTaskCompleted) == 0 ||
                   Volatile.Read(ref AsyncVoidCount) != 0 ||
                   Queue.Count != 0) {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) {
                    Interlocked.CompareExchange(ref CallbackException,
                        new TimeoutException("The test UI context did not finish within 15 seconds."), null);
                    return;
                }
                if (!Queue.TryTake(out var work, TimeSpan.FromMilliseconds(100))) {
                    continue;
                }
                try {
                    work.Callback(work.State);
                }
                catch (Exception exception) {
                    Interlocked.CompareExchange(ref CallbackException, exception, null);
                }
            }
        }

        public Exception GetCallbackException() =>
            Volatile.Read(ref CallbackException);
    }

    public static Task Run(Func<Task> action) =>
        Run(async () => {
            await action();
            return true;
        });

    public static Task<T> Run<T>(Func<Task<T>> action) {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunCore(action, result)) {
            IsBackground = true,
            Name = "Brows.Operations test UI thread"
        };
        thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static void RunCore<T>(Func<Task<T>> action, TaskCompletionSource<T> result) {
        var context = new PumpSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        Task<T> testTask;
        try {
            testTask = action();
        }
        catch (Exception exception) {
            testTask = Task.FromException<T>(exception);
        }

        testTask.ContinueWith(_ => context.TestCompleted(),
                              CancellationToken.None,
                              TaskContinuationOptions.ExecuteSynchronously,
                              TaskScheduler.Default);
        context.Run();

        var callbackException = context.GetCallbackException();
        if (callbackException is not null) {
            result.TrySetException(callbackException);
        }
        else if (testTask.IsCanceled) {
            result.TrySetCanceled();
        }
        else if (testTask.IsFaulted) {
            result.TrySetException(testTask.Exception!.InnerExceptions);
        }
        else {
            result.TrySetResult(testTask.GetAwaiter().GetResult());
        }
        SynchronizationContext.SetSynchronizationContext(null);
    }
}
