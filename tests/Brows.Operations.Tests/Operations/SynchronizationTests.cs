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

/// <summary>Tests context capture and collection mutations from worker continuations.</summary>
[TestFixture]
public sealed class SynchronizationTests {
    private sealed class QueuedSynchronizationContext : SynchronizationContext {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object State)> Queue = new();

        public int Pending => Queue.Count;

        public override void Post(SendOrPostCallback callback, object state) {
            Queue.Enqueue((callback, state));
        }

        public void RunPending() {
            var previous = Current;
            SetSynchronizationContext(this);
            try {
                while (Queue.TryDequeue(out var work)) {
                    work.Callback(work.State);
                }
            }
            finally {
                SetSynchronizationContext(previous);
            }
        }
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Verifies collection changes are posted without blocking the operation caller.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Manager_WorkerExecution_PostsChangesAsynchronously() => UiTestThread.Run(async () => {
        var context = new QueuedSynchronizationContext();
        var collection = new OperationCollection(new OperationSynchronization(context));
        var manager = new OperationManager(collection);
        var changes = new List<(NotifyCollectionChangedAction Action, SynchronizationContext Context)>();
        ((INotifyCollectionChanged)collection.Source).CollectionChanged += (_, e) =>
            changes.Add((e.Action, SynchronizationContext.Current));

        await Task.Run(() => manager.Operate("synchronous root", (_, _) => Task.CompletedTask)).WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(changes, Is.Empty);
            Assert.That(context.Pending, Is.EqualTo(1));
            Assert.That(collection.Count, Is.Zero);
        }
        context.RunPending();

        using (Assert.EnterMultipleScope()) {
            Assert.That(changes.Select(change => change.Action), Is.EqualTo(new[] {
                NotifyCollectionChangedAction.Add,
                NotifyCollectionChangedAction.Remove
            }));
            Assert.That(changes.All(change => change.Context == context), Is.True);
            Assert.That(collection.Count, Is.Zero);
        }
    });

    /// <summary>Verifies worker execution marshals root additions and automatic cleanup, but not count notifications.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Manager_WorkerExecution_UsesSuppliedContextForRootMutations() => UiTestThread.Run(async () => {
        var collection = new OperationCollection();
        IOperationCollection operations = collection;
        var context = SynchronizationContext.Current;
        var manager = new OperationManager(collection);
        var mutations = new List<SynchronizationContext>();
        var notifications = new List<SynchronizationContext>();
        var collectionChanges = NewSignal();
        ((INotifyCollectionChanged)((OperationCollection)operations).Source).CollectionChanged += (_, _) => {
            mutations.Add(SynchronizationContext.Current);
            if (mutations.Count == 2) {
                collectionChanges.TrySetResult(true);
            }
        };
        collection.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(IOperationCollection.Count)) {
                notifications.Add(SynchronizationContext.Current);
            }
        };
        await Task.Run(() => manager.Operate("worker root", (_, _) => Task.CompletedTask)).WaitAsync(Timeout);
        await collectionChanges.Task.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(mutations, Is.EqualTo(new[] { context, context }));
            Assert.That(notifications, Is.EqualTo(new SynchronizationContext[] { null, null }));
            Assert.That(operations.Count, Is.Zero);
        }
    });

    /// <summary>Verifies root and descendant collection changes use the context captured at creation.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Operate_UsesContextCapturedAtCreationForDescendantCollections() =>
        UiTestThread.Run(async () => {
                var context = SynchronizationContext.Current;
                var uiThread = Environment.CurrentManagedThreadId;
                IOperator @operator = new Operator(context);
                var release = NewSignal();
                var collectionChanges = NewSignal();
                var mutations = new ConcurrentQueue<(SynchronizationContext Context, int Thread, int Depth)>();
                Operation root = null;
                void Subscribe(OperationBase parent) {
                    ((INotifyCollectionChanged)parent.ChildSource).CollectionChanged += (_, e) => {
                        var child = (OperationBase)e.NewItems[0];
                        mutations.Enqueue((SynchronizationContext.Current, Environment.CurrentManagedThreadId, child.Depth));
                        if (mutations.Count == 6) {
                            collectionChanges.TrySetResult(true);
                        }
                        Subscribe(child);
                    };
                }
                var source = (INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source;
                NotifyCollectionChangedEventHandler changed = (_, e) => {
                    mutations.Enqueue((SynchronizationContext.Current, Environment.CurrentManagedThreadId, 0));
                    if (mutations.Count == 6) {
                        collectionChanges.TrySetResult(true);
                    }
                };
                source.CollectionChanged += changed;
                try {
                    OperationDelegate task = async (progress, _) => {
                        Assert.That(SynchronizationContext.Current, Is.SameAs(context));
                        await release.Task.ConfigureAwait(false);
                        await progress.Children(new[] { 1, 2 }, index => new OperationChild($"child {index}",
                            async (childProgress, _) => {
                                await Task.Run(async () => {
                                    await childProgress.Child("grandchild", (_, _) => Task.CompletedTask);
                                }, CancellationToken.None).ConfigureAwait(false);
                            })).ConfigureAwait(false);
                    };
                    @operator.Operate("root", task);
                    root = (Operation)@operator.Operations.Snapshot().Single();
                    Subscribe(root);
                    Assert.That(root, Is.Not.Null);
                    release.SetResult(true);
                    await root.Completion.WaitAsync(Timeout);
                    await collectionChanges.Task.WaitAsync(Timeout);
                    using (Assert.EnterMultipleScope()) {
                        Assert.That(mutations.Select(item => item.Depth).OrderBy(depth => depth), Is.EqualTo(new[] { 0, 0, 1, 1, 2, 2 }));
                        Assert.That(mutations.Where(item => item.Depth == 0)
                            .All(item => item.Context == context && item.Thread == uiThread), Is.True);
                        Assert.That(mutations.Where(item => item.Depth > 0)
                            .All(item => item.Context == context && item.Thread == uiThread), Is.True);
                        Assert.That(@operator.Operations.Count, Is.Zero);
                    }
                }
                finally {
                    release.TrySetResult(true);
                    source.CollectionChanged -= changed;
                }
        });

    /// <summary>Verifies all public and command removal paths use the operation's captured context.</summary>
    /// <param name="removal">The removal API to exercise.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase("collection")]
    [TestCase("command")]
    [TestCase("all")]
    [TestCase("errors")]
    public Task WorkerRemoval_UsesCapturedContext(string removal) =>
        UiTestThread.Run(async () => {
            IOperator @operator = new Operator();
            var context = SynchronizationContext.Current;
            var uiThread = Environment.CurrentManagedThreadId;
            @operator.Operate("failed", (_, _) => Task.FromException(new IOException("failure")));
            var root = (Operation)@operator.Operations.Snapshot().Single();
            var source = (INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source;
            var rootAdded = NewSignal();
            NotifyCollectionChangedEventHandler added = (_, e) => {
                if (e.Action == NotifyCollectionChangedAction.Add) {
                    rootAdded.TrySetResult(true);
                }
            };
            source.CollectionChanged += added;
            await rootAdded.Task.WaitAsync(Timeout);
            source.CollectionChanged -= added;
            var collectionChanged = NewSignal();
            SynchronizationContext mutationContext = null;
            var mutationThread = 0;
            var count = 0;
            source.CollectionChanged += (_, _) => {
                mutationContext = SynchronizationContext.Current;
                mutationThread = Environment.CurrentManagedThreadId;
                count++;
                collectionChanged.TrySetResult(true);
            };
            var workerThread = 0;
            var removed = await Task.Run(() => {
                workerThread = Environment.CurrentManagedThreadId;
                switch (removal) {
                    case "collection": return @operator.Operations.Remove(root) ? 1 : 0;
                    case "command": root.Remove(); return 1;
                    case "errors": return @operator.Operations.RemoveComplete(withError: true);
                    default: return @operator.Operations.RemoveComplete();
                }
            }).WaitAsync(Timeout);
            await collectionChanged.Task.WaitAsync(Timeout);
            using (Assert.EnterMultipleScope()) {
                Assert.That(removed, Is.EqualTo(1));
                Assert.That(count, Is.EqualTo(1));
                Assert.That(mutationContext, Is.SameAs(context));
                Assert.That(mutationThread, Is.EqualTo(uiThread));
                Assert.That(@operator.Operations.Count, Is.Zero);
            }
        });

    /// <summary>Verifies progress properties and notifications remain on the worker reporting them.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task WorkerProgress_DoesNotMarshalPropertyNotifications() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = NewSignal();
        var workerThread = 0;
        var notifications = new List<(SynchronizationContext Context, int Thread)>();
        @operator.Operate("root", async (progress, _) => {
            await release.Task.ConfigureAwait(false);
            workerThread = Environment.CurrentManagedThreadId;
            progress.Change(name: "worker", data: "details", setProgress: 1, setTarget: 2);
        });
        var root = (Operation)@operator.Operations.Snapshot().Single();
        root.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(OperationBase.Name) or nameof(OperationBase.Data) or
                nameof(OperationBase.Progress) or nameof(OperationBase.Target)) {
                notifications.Add((SynchronizationContext.Current, Environment.CurrentManagedThreadId));
            }
        };
        release.SetResult(true);
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(notifications.Count, Is.EqualTo(4));
            Assert.That(notifications.All(item => item.Context is null && item.Thread == workerThread), Is.True);
            Assert.That(root.Progress, Is.EqualTo(1));
            Assert.That(root.Target, Is.EqualTo(2));
        }
    });

    /// <summary>
    /// Verifies sequential no-context collection projection drains synchronously and worker child registration works.
    /// </summary>
    /// <returns>
    /// A task representing execution of the test.
    /// </returns>
    [Test]
    public Task Operate_WithoutContext_SequentialCollectionChangesDrainSynchronously() => Task.Run(async () => {
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

    /// <summary>
    /// Verifies concurrent no-context producers queue projection on the active drainer thread.
    /// </summary>
    /// <returns>
    /// A task representing execution of the test.
    /// </returns>
    [Test]
    public async Task Operate_WithoutContext_OverlappingProducersSerializeCollectionProjection() {
        var @operator = await Task.Run(() => {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try {
                return (IOperator)new Operator();
            }
            finally {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }).WaitAsync(Timeout);
        var collection = (OperationCollection)@operator.Operations;
        var finish = NewSignal();
        var firstEventEntered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstEvent = NewSignal();
        var secondEventDelivered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstProducerReturned = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondProducerReturned =
            new TaskCompletionSource<(int Thread, string[] CoreNames, string[] ObservableNames)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new ConcurrentQueue<(NotifyCollectionChangedAction Action, string Name, int Thread)>();
        var source = (INotifyCollectionChanged)collection.Source;
        source.CollectionChanged += (_, e) => {
            var item = (Operation)(e.NewItems ?? e.OldItems)[0];
            var thread = Environment.CurrentManagedThreadId;
            changes.Enqueue((e.Action, item.Name, thread));
            if (e.Action == NotifyCollectionChangedAction.Add && item.Name == "first") {
                firstEventEntered.TrySetResult(thread);
                releaseFirstEvent.Task.GetAwaiter().GetResult();
            }
            if (e.Action == NotifyCollectionChangedAction.Add && item.Name == "second") {
                secondEventDelivered.TrySetResult(thread);
            }
        };
        var firstProducer = new Thread(() => {
            SynchronizationContext.SetSynchronizationContext(null);
            var thread = Environment.CurrentManagedThreadId;
            try {
                @operator.Operate("first", async (_, _) => await finish.Task.ConfigureAwait(false));
                firstProducerReturned.TrySetResult(thread);
            }
            catch (Exception exception) {
                firstProducerReturned.TrySetException(exception);
            }
        }) {
            IsBackground = true,
            Name = "First collection producer"
        };
        var secondProducer = new Thread(() => {
            SynchronizationContext.SetSynchronizationContext(null);
            var thread = Environment.CurrentManagedThreadId;
            try {
                @operator.Operate("second", async (_, _) => await finish.Task.ConfigureAwait(false));
                var coreNames = @operator.Operations.Snapshot()
                    .Select(operation => ((Operation)operation).Name)
                    .ToArray();
                var observableNames = ((IEnumerable)collection.Source).Cast<Operation>()
                    .Select(operation => operation.Name)
                    .ToArray();
                secondProducerReturned.TrySetResult((thread, coreNames, observableNames));
            }
            catch (Exception exception) {
                secondProducerReturned.TrySetException(exception);
            }
        }) {
            IsBackground = true,
            Name = "Second collection producer"
        };

        try {
            firstProducer.Start();
            var drainerThread = await firstEventEntered.Task.WaitAsync(Timeout);
            secondProducer.Start();
            var secondResult = await secondProducerReturned.Task.WaitAsync(Timeout);
            var changesBeforeRelease = changes.ToArray();
            using (Assert.EnterMultipleScope()) {
                Assert.That(secondResult.Thread, Is.Not.EqualTo(drainerThread));
                Assert.That(secondResult.CoreNames, Is.EquivalentTo(new[] { "first", "second" }));
                Assert.That(secondResult.ObservableNames, Is.EqualTo(new[] { "first" }));
                Assert.That(changesBeforeRelease.Select(change => change.Name), Is.EqualTo(new[] { "first" }));
            }

            releaseFirstEvent.TrySetResult(true);
            var secondEventThread = await secondEventDelivered.Task.WaitAsync(Timeout);
            var firstReturnThread = await firstProducerReturned.Task.WaitAsync(Timeout);
            var additions = changes.Where(change => change.Action == NotifyCollectionChangedAction.Add).ToArray();
            using (Assert.EnterMultipleScope()) {
                Assert.That(firstReturnThread, Is.EqualTo(drainerThread));
                Assert.That(additions.Select(change => change.Name), Is.EqualTo(new[] { "first", "second" }));
                Assert.That(additions.All(change => change.Thread == drainerThread), Is.True);
                Assert.That(secondEventThread, Is.EqualTo(drainerThread));
                Assert.That(secondEventThread, Is.Not.EqualTo(secondResult.Thread));
            }

            var completions = @operator.Operations.Snapshot()
                .Select(operation => ((Operation)operation).Completion)
                .ToArray();
            finish.TrySetResult(true);
            await Task.WhenAll(completions).WaitAsync(Timeout);
        }
        finally {
            releaseFirstEvent.TrySetResult(true);
            finish.TrySetResult(true);
            if (firstProducer.IsAlive) {
                Assert.That(
                    firstProducer.Join(Timeout),
                    Is.True,
                    "The first producer should exit within the timeout.");
            }
            if (secondProducer.IsAlive) {
                Assert.That(
                    secondProducer.Join(Timeout),
                    Is.True,
                    "The second producer should exit within the timeout.");
            }
        }
    }
}
