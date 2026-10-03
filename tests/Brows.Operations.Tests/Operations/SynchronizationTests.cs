using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Brows.Operations;

/// <summary>Tests that operator state changes are marshalled to the operator's synchronization context.</summary>
[TestFixture]
public sealed class SynchronizationTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class CountingContext : SynchronizationContext {
        private readonly SynchronizationContext Inner;
        private int PostCount;

        public int Posts => Volatile.Read(ref PostCount);

        public CountingContext(SynchronizationContext inner) {
            Inner = inner;
        }

        public override void Post(SendOrPostCallback callback, object state) {
            Interlocked.Increment(ref PostCount);
            Inner.Post(callback, state);
        }

        public override void Send(SendOrPostCallback callback, object state) {
            Inner.Send(callback, state);
        }
    }

    private sealed class ManualContext : SynchronizationContext {
        public ConcurrentQueue<(SendOrPostCallback Callback, object State)> Queue { get; } = new();

        public override void Post(SendOrPostCallback callback, object state) {
            Queue.Enqueue((callback, state));
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Verifies worker execution marshals root additions, cleanup, and notifications.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Manager_WorkerExecution_MarshalsRootMutationsAndNotifications() => UiTestThread.Run(async () => {
        var collection = new OperationCollection();
        IOperationCollection operations = collection;
        var context = SynchronizationContext.Current;
        var manager = new OperationManager(collection);
        var mutations = new List<SynchronizationContext>();
        var notifications = new List<SynchronizationContext>();
        var removed = NewSignal();
        ((INotifyCollectionChanged)collection.Source).CollectionChanged += (_, e) => {
            mutations.Add(SynchronizationContext.Current);
            if (e.Action == NotifyCollectionChangedAction.Remove) {
                removed.TrySetResult(true);
            }
        };
        collection.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(IOperationCollection.Count)) {
                notifications.Add(SynchronizationContext.Current);
            }
        };
        await Task.Run(() => manager.Operate("worker root", (_, _) => Task.CompletedTask)).WaitAsync(Timeout);
        await removed.Task.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(mutations, Is.EqualTo(new[] { context, context }));
            Assert.That(notifications, Is.EqualTo(new[] { context, context }));
            Assert.That(operations.Count, Is.Zero);
        }
    });

    /// <summary>Verifies collection changes for all descendants run on the operator's context.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Operate_MarshalsDescendantCollectionChangesToContext() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var context = SynchronizationContext.Current;
        var uiThread = Environment.CurrentManagedThreadId;
        var release = NewSignal();
        var mutations = new ConcurrentQueue<(SynchronizationContext Context, int Thread, int Depth)>();
        Operation root = null;
        void Subscribe(OperationBase parent) {
            ((INotifyCollectionChanged)parent.ChildSource).CollectionChanged += (_, e) => {
                var child = (OperationBase)e.NewItems[0];
                mutations.Enqueue((SynchronizationContext.Current, Environment.CurrentManagedThreadId, child.Depth));
                Subscribe(child);
            };
        }
        ((INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source).CollectionChanged += (_, e) => {
            mutations.Enqueue((SynchronizationContext.Current, Environment.CurrentManagedThreadId, 0));
            if (e.Action == NotifyCollectionChangedAction.Add) {
                root = (Operation)e.NewItems[0];
                Subscribe(root);
            }
        };
        try {
            @operator.Operate("root", async (progress, _) => {
                Assert.That(SynchronizationContext.Current, Is.SameAs(context));
                await release.Task.ConfigureAwait(false);
                await progress.Children(new[] { 1, 2 }, index => new OperationChild($"child {index}",
                    async (childProgress, _) => {
                        await Task.Run(async () => {
                            await childProgress.Child("grandchild", (_, _) => Task.CompletedTask);
                        }, CancellationToken.None).ConfigureAwait(false);
                    })).ConfigureAwait(false);
            });
            Assert.That(root, Is.Not.Null);
            release.SetResult(true);
            await root.Completion.WaitAsync(Timeout);
            await Task.Yield();
            using (Assert.EnterMultipleScope()) {
                Assert.That(mutations.Select(item => item.Depth).OrderBy(depth => depth),
                            Is.EqualTo(new[] { 0, 0, 1, 1, 2, 2 }));
                Assert.That(mutations.All(item => item.Context == context && item.Thread == uiThread), Is.True);
                Assert.That(@operator.Operations.Count, Is.Zero);
            }
        }
        finally {
            release.TrySetResult(true);
        }
    });

    /// <summary>Verifies all public and command removal paths run on the operator's context.</summary>
    /// <param name="removal">The removal API to exercise.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase("collection")]
    [TestCase("command")]
    [TestCase("all")]
    [TestCase("errors")]
    public Task WorkerRemoval_RunsOnContext(string removal) =>
        UiTestThread.Run(async () => {
            IOperator @operator = new Operator();
            var context = SynchronizationContext.Current;
            var uiThread = Environment.CurrentManagedThreadId;
            @operator.Operate("failed", (_, _) => Task.FromException(new IOException("failure")));
            var root = (Operation)@operator.Operations.Snapshot().Single();
            SynchronizationContext mutationContext = null;
            var mutationThread = 0;
            var count = 0;
            ((INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source).CollectionChanged += (_, _) => {
                mutationContext = SynchronizationContext.Current;
                mutationThread = Environment.CurrentManagedThreadId;
                count++;
            };
            var removed = await Task.Run(() => {
                switch (removal) {
                    case "collection": return @operator.Operations.Remove(root) ? 1 : 0;
                    case "command": root.Remove(); return 1;
                    case "errors": return @operator.Operations.RemoveComplete(withError: true);
                    default: return @operator.Operations.RemoveComplete();
                }
            }).WaitAsync(Timeout);
            using (Assert.EnterMultipleScope()) {
                Assert.That(removed, Is.EqualTo(1));
                Assert.That(count, Is.EqualTo(1));
                Assert.That(mutationContext, Is.SameAs(context));
                Assert.That(mutationThread, Is.EqualTo(uiThread));
                Assert.That(@operator.Operations.Count, Is.Zero);
            }
        });

    /// <summary>Verifies progress reported on a worker is applied and notified on the operator's context.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task WorkerProgress_MarshalsPropertyNotificationsToContext() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var uiThread = Environment.CurrentManagedThreadId;
        var release = NewSignal();
        var workerThread = 0;
        var notifications = new List<int>();
        @operator.Operate("root", async (progress, _) => {
            await release.Task.ConfigureAwait(false);
            workerThread = Environment.CurrentManagedThreadId;
            progress.Change(name: "worker", data: "details", setProgress: 1, setTarget: 2);
        });
        var root = (Operation)@operator.Operations.Snapshot().Single();
        root.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(OperationBase.Name) or nameof(OperationBase.Data) or
                nameof(OperationBase.Progress) or nameof(OperationBase.Target)) {
                notifications.Add(Environment.CurrentManagedThreadId);
            }
        };
        release.SetResult(true);
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(workerThread, Is.Not.EqualTo(uiThread));
            Assert.That(notifications, Is.EqualTo(new[] { uiThread, uiThread, uiThread, uiThread }));
            Assert.That(root.Name, Is.EqualTo("worker"));
            Assert.That(root.Data, Is.EqualTo("details"));
            Assert.That(root.Progress, Is.EqualTo(1));
            Assert.That(root.Target, Is.EqualTo(2));
        }
    });

    /// <summary>Verifies worker reports are coalesced into one post with sequentially correct values.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task WorkerProgress_CoalescesReports() => UiTestThread.Run(async () => {
        var context = new CountingContext(SynchronizationContext.Current);
        var @operator = new Operator(context);
        var done = NewSignal();
        IOperationProgress reporter = null;
        @operator.Operate("root", async (progress, _) => {
            reporter = progress;
            await done.Task;
        });
        var root = (Operation)@operator.Operations.Snapshot().Single();
        var collection = (OperationCollection)@operator.Operations;
        var posts = context.Posts;
        /*
         * Block the context while the worker reports, so every report is pending at once.
         */
        Task.Run(() => {
            reporter.Change(setTarget: 100, name: "renamed");
            for (var i = 0; i < 1000; i++) {
                reporter.Change(addProgress: 1, progressString: "adding");
            }
            reporter.Change(setProgress: 50);
            reporter.Change(addProgress: 3);
            reporter.Change(addProgress: 1, progressString: "final", data: "d");
        }).Wait(Timeout);
        var postsWhileBlocked = context.Posts - posts;
        collection.Context.Invoke(() => { });
        using (Assert.EnterMultipleScope()) {
            Assert.That(postsWhileBlocked, Is.EqualTo(1));
            Assert.That(root.Progress, Is.EqualTo(54));
            Assert.That(root.Target, Is.EqualTo(100));
            Assert.That(root.ProgressString, Is.EqualTo("final"));
            Assert.That(root.TargetString, Is.EqualTo("100"));
            Assert.That(root.Name, Is.EqualTo("renamed"));
            Assert.That(root.Data, Is.EqualTo("d"));
        }
        done.SetResult(true);
        await root.Completion.WaitAsync(Timeout);
    });

    /// <summary>Verifies a worker report and an unawaited child apply in order before the parent closes.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task WorkerChange_ThenUnawaitedChild_AreOrderedAndRegistered() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = NewSignal();
        var events = new List<string>();
        Task child = null;
        @operator.Operate("root", async (progress, _) => {
            await release.Task.ConfigureAwait(false);
            progress.Change(name: "before child");
            child = progress.Child("late", async (_, _) => await Task.Yield());
        });
        var root = (Operation)@operator.Operations.Snapshot().Single();
        root.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(OperationBase.Name)) {
                events.Add("name");
            }
        };
        ((INotifyCollectionChanged)root.ChildSource).CollectionChanged += (_, _) => events.Add("child");
        release.SetResult(true);
        await root.Completion.WaitAsync(Timeout);
        await child.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(events, Is.EqualTo(new[] { "name", "child" }));
            Assert.That(root.Error, Is.Null);
            Assert.That(((IEnumerable)root.ChildSource).Cast<OperationBase>().Single().Complete, Is.True);
        }
    });

    /// <summary>Verifies worker child registration after cancellation or after the parent returns fails.</summary>
    /// <param name="cancel">Whether to cancel the parent before registration.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(true)]
    [TestCase(false)]
    public Task WorkerChild_AfterCancelOrClose_Fails(bool cancel) => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = NewSignal();
        var registered = NewSignal();
        var finish = NewSignal();
        IOperationProgress reporter = null;
        Operation root = null;
        ((INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source).CollectionChanged += (_, e) => {
            if (e.Action == NotifyCollectionChangedAction.Add) {
                root = (Operation)e.NewItems[0];
            }
        };
        @operator.Operate("root", async (progress, _) => {
            reporter = progress;
            if (cancel) {
                await release.Task.ConfigureAwait(false);
                registered.TrySetResult(true);
                await finish.Task.ConfigureAwait(false);
            }
        });
        Type expected;
        if (cancel) {
            release.SetResult(true);
            await registered.Task.WaitAsync(Timeout);
            root.Cancel();
            expected = typeof(OperationCanceledException);
        }
        else {
            expected = typeof(InvalidOperationException);
        }
        var error = await Task.Run(async () => {
            try {
                await reporter.Child("too late", (_, _) => Task.CompletedTask);
                return null;
            }
            catch (Exception ex) {
                return ex;
            }
        }).WaitAsync(Timeout);
        finish.SetResult(true);
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(error, Is.InstanceOf(expected));
            Assert.That(root.Canceling, Is.EqualTo(cancel));
            Assert.That(root.ChildSource, Is.Empty);
        }
    });

    /// <summary>Verifies Operate starts synchronously on the context and on the thread pool from a worker.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Operate_FromWorker_RegistersOnContextAndRunsOffContext() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var uiThread = Environment.CurrentManagedThreadId;
        var syncThread = 0;
        @operator.Operate("sync", (_, _) => {
            syncThread = Environment.CurrentManagedThreadId;
            return Task.CompletedTask;
        });
        Assert.That(syncThread, Is.EqualTo(uiThread));
        var delegateThread = NewSignal();
        var addThread = 0;
        var started = 0;
        ((INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source).CollectionChanged += (_, e) => {
            if (e.Action == NotifyCollectionChangedAction.Add) {
                addThread = Environment.CurrentManagedThreadId;
            }
        };
        await Task.Run(() => @operator.Operate("worker", (_, _) => {
            Interlocked.Increment(ref started);
            delegateThread.TrySetResult(Environment.CurrentManagedThreadId != uiThread);
            return Task.CompletedTask;
        })).WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(await delegateThread.Task.WaitAsync(Timeout), Is.True);
            Assert.That(addThread, Is.EqualTo(uiThread));
            Assert.That(started, Is.EqualTo(1));
        }
    });

    /// <summary>Verifies cancellation that completes descendants inline does not dispose sources prematurely.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Cancel_WithInlineDescendantCompletion_CompletesWithoutError() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var started = 0;
        var allStarted = NewSignal();
        static async Task WaitForCancel(CancellationToken token) {
            var canceled = new TaskCompletionSource<bool>();
            using (token.Register(() => canceled.TrySetResult(true))) {
                await canceled.Task.ConfigureAwait(false);
            }
        }
        /*
         * Start from a worker so the delegates resume inline on the canceling thread.
         */
        await Task.Run(() => @operator.Operate("root", async (progress, token) => {
            await progress.Children(Enumerable.Range(0, 3), index => new OperationChild($"child {index}",
                async (childProgress, childToken) => {
                    await childProgress.Child("grandchild", async (_, grandchildToken) => {
                        if (Interlocked.Increment(ref started) == 3) {
                            allStarted.TrySetResult(true);
                        }
                        await WaitForCancel(grandchildToken);
                    }).ConfigureAwait(false);
                    await WaitForCancel(childToken);
                })).ConfigureAwait(false);
            await WaitForCancel(token);
        })).WaitAsync(Timeout);
        var root = (Operation)@operator.Operations.Snapshot().Single();
        await allStarted.Task.WaitAsync(Timeout);
        Assert.DoesNotThrow(root.Cancel);
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(root.Complete, Is.True);
            Assert.That(root.Error, Is.Null);
            Assert.That(root.CompleteWithError, Is.False);
        }
    });

    /// <summary>Verifies that without a context concurrent reports keep ancestor totals consistent.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Operate_WithoutContext_ConcurrentReportsRollUpConsistently() => Task.Run(async () => {
        Assert.That(SynchronizationContext.Current, Is.Null);
        IOperator @operator = new Operator();
        Operation root = null;
        ((INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source).CollectionChanged += (_, e) => {
            if (e.Action == NotifyCollectionChangedAction.Add) {
                root = (Operation)e.NewItems[0];
            }
        };
        @operator.Operate("root", async (progress, _) => {
            await progress.Children(Enumerable.Range(0, 4), index => new OperationChild($"child {index}",
                (childProgress, _) => Task.Run(() => {
                    for (var i = 0; i < 1000; i++) {
                        childProgress.Change(addTarget: 1);
                        childProgress.Change(addProgress: 1);
                    }
                }, CancellationToken.None)));
        });
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(root.Progress, Is.EqualTo(4000));
            Assert.That(root.Target, Is.EqualTo(4000));
        }
    });

    /// <summary>Verifies an absent context permits synchronous collection changes and worker child registration.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Operate_WithoutContext_MutatesCollectionsOnCallingThread() => Task.Run(async () => {
        Assert.That(SynchronizationContext.Current, Is.Null);
        IOperator @operator = new Operator();
        var release = NewSignal();
        Operation root = null;
        var registrationThread = 0;
        var rootContexts = new List<SynchronizationContext>();
        ((INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source).CollectionChanged += (_, e) => {
            rootContexts.Add(SynchronizationContext.Current);
            if (e.Action == NotifyCollectionChangedAction.Add) {
                root = (Operation)e.NewItems[0];
            }
        };
        @operator.Operate("root", async (progress, _) => {
            await release.Task.ConfigureAwait(false);
            registrationThread = Environment.CurrentManagedThreadId;
            await progress.Child("child", (_, _) => Task.CompletedTask);
        });
        var childThread = 0;
        ((INotifyCollectionChanged)root.ChildSource).CollectionChanged += (_, _) => {
            childThread = Environment.CurrentManagedThreadId;
        };
        release.SetResult(true);
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(rootContexts, Is.EqualTo(new SynchronizationContext[] { null, null }));
            Assert.That(childThread, Is.EqualTo(registrationThread).And.Not.Zero);
            Assert.That(((IEnumerable)root.ChildSource).Cast<OperationBase>().Single().Complete, Is.True);
            Assert.That(@operator.Operations.Count, Is.Zero);
        }
    });

    /// <summary>Verifies queued work still runs after an earlier queued item throws.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public async Task QueuedWork_AfterThrowingItem_StillRuns() {
        var manual = new ManualContext();
        var context = new OperationContext(manual);
        var ran = false;
        await Task.Run(() => {
            context.Invoke(() => throw new IOException("first"));
            context.Invoke(() => ran = true);
        }).WaitAsync(Timeout);
        Assert.That(manual.Queue.TryDequeue(out var first), Is.True);
        Assert.Throws<IOException>(() => first.Callback(first.State));
        Assert.That(ran, Is.False);
        Assert.That(manual.Queue.TryDequeue(out var second), Is.True);
        second.Callback(second.State);
        using (Assert.EnterMultipleScope()) {
            Assert.That(ran, Is.True);
            Assert.That(manual.Queue, Is.Empty);
        }
    }
}