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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Verifies worker execution marshals root additions and automatic cleanup, but not count notifications.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Manager_WorkerExecution_UsesSuppliedContextForRootMutations() => UiTestThread.Run(async () => {
        var collection = new OperationCollection();
        IOperationCollection operations = collection;
        var context = SynchronizationContext.Current;
        var manager = new OperationManager(collection, context);
        var mutations = new List<SynchronizationContext>();
        var notifications = new List<SynchronizationContext>();
        ((INotifyCollectionChanged)((OperationCollection)operations).Source).CollectionChanged += (_, _) =>
            mutations.Add(SynchronizationContext.Current);
        collection.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(IOperationCollection.Count)) {
                notifications.Add(SynchronizationContext.Current);
            }
        };
        await Task.Run(() => manager.Operate("worker root", (_, _) => Task.CompletedTask)).WaitAsync(Timeout);
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
                var mutations = new ConcurrentQueue<(SynchronizationContext Context, int Thread, int Depth)>();
                Operation root = null;
                void Subscribe(OperationBase parent) {
                    ((INotifyCollectionChanged)parent.ChildSource).CollectionChanged += (_, e) => {
                        var child = (OperationBase)e.NewItems[0];
                        mutations.Enqueue((SynchronizationContext.Current, Environment.CurrentManagedThreadId, child.Depth));
                        Subscribe(child);
                    };
                }
                var source = (INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source;
                NotifyCollectionChangedEventHandler changed = (_, e) => {
                    mutations.Enqueue((SynchronizationContext.Current, Environment.CurrentManagedThreadId, 0));
                    if (e.Action == NotifyCollectionChangedAction.Add) {
                        root = (Operation)e.NewItems[0];
                        Subscribe(root);
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
                    Assert.That(root, Is.Not.Null);
                    release.SetResult(true);
                    await root.Completion.WaitAsync(Timeout);
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
            SynchronizationContext mutationContext = null;
            var mutationThread = 0;
            var count = 0;
            ((INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source).CollectionChanged += (_, _) => {
                mutationContext = SynchronizationContext.Current;
                mutationThread = Environment.CurrentManagedThreadId;
                count++;
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
}
