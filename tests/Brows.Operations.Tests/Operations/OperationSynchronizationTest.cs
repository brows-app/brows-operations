using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Brows.Operations;

/// <summary>
/// Tests notification batching across nested and reentrant state updates.
/// </summary>
[TestFixture]
public sealed class OperationSynchronizationTest {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Verifies nested updates release every acquired state gate before invoking subscribers.
    /// </summary>
    [Test]
    public void NestedUpdates_ReleaseAllStateGatesBeforeNotifications() {
        var first = new OperationSynchronization(null);
        var second = new OperationSynchronization(null);
        var value = 0;
        var observed = 0;
        first.Update(() => {
            value = 1;
            second.Update(() => first.Notify(() => {
                observed = Task.Run(() => first.Read(() => second.Read(() => value)))
                    .WaitAsync(Timeout).GetAwaiter().GetResult();
            }));
            value = 2;
        });

        Assert.That(observed, Is.EqualTo(2));
    }

    /// <summary>
    /// Verifies reentrant updates append notifications without interrupting the current subscriber.
    /// </summary>
    [Test]
    public void ReentrantUpdates_PreserveNotificationOrder() {
        var synchronization = new OperationSynchronization(null);
        var notifications = new List<string>();
        synchronization.Update(() => {
            synchronization.Notify(() => {
                notifications.Add("first entered");
                synchronization.Update(() => synchronization.Notify(() => notifications.Add("reentrant")));
                notifications.Add("first returned");
            });
            synchronization.Notify(() => notifications.Add("second"));
        });

        Assert.That(notifications, Is.EqualTo(new[] { "first entered", "first returned", "second", "reentrant" }));
    }

    /// <summary>
    /// Verifies concurrent updates publish on their own threads while another subscriber is blocked.
    /// </summary>
    /// <returns>
    /// A task representing execution of the test.
    /// </returns>
    [Test]
    public async Task ConcurrentUpdates_PublishIndependentlyOnTheirUpdatingThreads() {
        var synchronization = new OperationSynchronization(null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstThread = 0;
        var secondThread = 0;
        var notificationThread = 0;
        var first = Task.Run(() => synchronization.Update(() => synchronization.Notify(() => {
            firstThread = Thread.CurrentThread.ManagedThreadId;
            entered.TrySetResult();
            release.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
        })));
        try {
            await entered.Task.WaitAsync(Timeout);
            await Task.Run(() => {
                secondThread = Thread.CurrentThread.ManagedThreadId;
                synchronization.Update(() => synchronization.Notify(() =>
                    notificationThread = Thread.CurrentThread.ManagedThreadId));
            }).WaitAsync(Timeout);
        }
        finally {
            release.TrySetResult();
            await first.WaitAsync(Timeout);
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(secondThread, Is.Not.EqualTo(firstThread));
            Assert.That(notificationThread, Is.EqualTo(secondThread));
        }
    }

    /// <summary>
    /// Verifies listener failures propagate while committed collection changes remain scheduled.
    /// </summary>
    [Test]
    public void ListenerFailure_SchedulesCommittedCollectionsAndClearsTheBatch() {
        var first = new OperationSynchronization(null);
        var second = new OperationSynchronization(null);
        var error = new IOException("listener");
        var changes = new List<string>();
        var observed = Assert.Throws<IOException>(() => first.Update(() => {
            first.Notify(() => throw error);
            second.Update(() => { }, () => changes.Add("second collection"));
        }, () => changes.Add("first collection")));
        first.Update(() => first.Notify(() => changes.Add("next notification")));

        using (Assert.EnterMultipleScope()) {
            Assert.That(observed, Is.SameAs(error));
            Assert.That(changes, Is.EqualTo(new[] { "second collection", "first collection", "next notification" }));
        }
    }
}
