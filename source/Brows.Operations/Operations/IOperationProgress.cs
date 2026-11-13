using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Brows.Operations;

/// <summary>
/// Reports operation state and starts child operations.
/// </summary>
/// <remarks>
/// Progress reports and child registration may run from any thread. Operation state changes are
/// synchronized across the tree. Observable collection changes are posted asynchronously to the
/// context captured when the operator was created. Without a captured context, the producer that
/// starts a serialized queue drain projects changes synchronously. A concurrent or reentrant update
/// can return before its queued projection, whose event runs on the active drainer thread.
/// Property-change notifications run on the thread updating state; WPF bindings marshal bound
/// property updates to their dispatcher. State is committed before notifications, and subscribers
/// run after state locks have been released. Reentrant reports append notifications to the current
/// thread's notification batch. Register children before the operation's delegate returns; the
/// operation waits for all registered descendants.
/// </remarks>
public interface IOperationProgress {
    /// <summary>
    /// Updates numeric progress, display strings, and operation metadata.
    /// </summary>
    /// <param name="addProgress">
    /// An amount to add to progress after applying <paramref name="setProgress"/>.
    /// </param>
    /// <param name="setProgress">
    /// An absolute progress value, or null to leave it unchanged before any addition.
    /// </param>
    /// <param name="addTarget">
    /// An amount to add to the target after applying <paramref name="setTarget"/>.
    /// </param>
    /// <param name="setTarget">
    /// An absolute target value, or null to leave it unchanged before any addition.
    /// </param>
    /// <param name="progressString">
    /// A progress display override, or null to use the numeric value formatted according to the progress kind.
    /// </param>
    /// <param name="targetString">
    /// A target display override, or null to use the numeric value formatted according to the progress kind.
    /// </param>
    /// <param name="name">
    /// A new display name, or null to leave the name unchanged.
    /// </param>
    /// <param name="data">
    /// New detail text, or null to leave the detail text unchanged.
    /// </param>
    /// <param name="kind">
    /// The unit for reported progress, target, and rate values, or null to leave the current kind unchanged.
    /// Use <see cref="OperationProgressKind.FileSize"/> when values are byte counts.
    /// </param>
    /// <param name="progressFormat">
    /// The numeric format string for scaled file-size progress, or null to leave the current format unchanged.
    /// Counts below 1 KiB are displayed as whole bytes.
    /// </param>
    /// <param name="targetFormat">
    /// The numeric format string for scaled file-size targets, or null to leave the current format unchanged.
    /// Counts below 1 KiB are displayed as whole bytes.
    /// </param>
    /// <param name="progressRateFormat">
    /// The numeric format string for the current progress rate, or null to leave the current format unchanged.
    /// File-size rates use the same automatically scaled binary units as file-size progress; values below 1 KiB
    /// are displayed as whole bytes.
    /// </param>
    /// <param name="progressRateAverageFormat">
    /// The numeric format string for the average progress rate, or null to leave the current format unchanged.
    /// File-size rates use the same automatically scaled binary units as file-size progress; values below 1 KiB
    /// are displayed as whole bytes.
    /// </param>
    /// <param name="formatProvider">
    /// The provider used to format file-size values and progress rates, or null to leave the current provider
    /// unchanged. When no provider has been set, the current culture is used.
    /// </param>
    /// <remarks>
    /// Numeric values represent caller-defined units. Changes are aggregated into each ancestor;
    /// setting a child's value adjusts ancestors by the difference. Set values are applied before
    /// additions, and numeric values throughout the hierarchy are committed before their notifications.
    /// Every call replaces both display strings, including calls that only update metadata. A numeric
    /// report starts a delay of about one second, after which still-running work becomes relevant for
    /// display. Errors become relevant immediately.
    /// </remarks>
    void Change(long? addProgress = null,
                long? setProgress = null,
                long? addTarget = null,
                long? setTarget = null,
                string progressString = null,
                string targetString = null,
                string name = null,
                string data = null,
                OperationProgressKind? kind = null,
                string progressFormat = null,
                string targetFormat = null,
                string progressRateFormat = null,
                string progressRateAverageFormat = null,
                IFormatProvider formatProvider = null);

    /// <summary>
    /// Starts a child operation and waits for its delegate and registered descendants to finish.
    /// </summary>
    /// <param name="name">
    /// The child's initial display name.
    /// </param>
    /// <param name="task">
    /// The delegate that performs the child's work.
    /// </param>
    /// <param name="failureBehavior">
    /// Whether ordinary work failures are returned or propagated after completion.
    /// </param>
    /// <param name="cancellationToken">
    /// An additional token linked to this child and its descendants.
    /// </param>
    /// <returns>
    /// A task that returns the child's outcome after its complete operation lifetime ends.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="task"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="failureBehavior"/> is undefined.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The parent delegate has already returned.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The parent or supplied token is canceled, or the child subtree is canceled under Throw policy.
    /// </exception>
    /// <exception cref="Exception">
    /// An infrastructure error occurs, or the child subtree fails under Throw policy.
    /// </exception>
    /// <remarks>
    /// Registration takes effect immediately in the operation tree. The child delegate starts
    /// synchronously through its first asynchronous wait on the calling thread. With a captured
    /// context, its observable collection addition is posted asynchronously to that context. Without
    /// one, the producer that starts a serialized queue drain projects the addition synchronously. A
    /// concurrent or reentrant call can return before its queued addition is projected; that event runs
    /// on the active drainer thread. The result describes work, while the operation lifetime includes
    /// registered descendants and asynchronous cleanup represented by the delegate's returned task.
    /// Record returns ordinary failure and child-only cancellation outcomes. Throw propagates them
    /// after completion. Parent or supplied-token cancellation and infrastructure failures propagate
    /// under either policy. Failures remain recorded in the operation tree if the caller catches them.
    /// Keep ownership of staged resources until the returned result has a Kind of Succeeded.
    /// </remarks>
    Task<OperationResult> Child(string name,
                                OperationDelegate task,
                                OperationFailureBehavior failureBehavior,
                                CancellationToken cancellationToken);

    /// <summary>
    /// Starts children from a sequence and waits for every started child to finish.
    /// </summary>
    /// <typeparam name="T">
    /// The type of source item used to describe each child.
    /// </typeparam>
    /// <param name="source">
    /// The items to enumerate for child creation.
    /// </param>
    /// <param name="child">
    /// A factory that returns a child description, or null to skip an item.
    /// </param>
    /// <param name="failureBehavior">
    /// Whether ordinary child failures are returned or propagated after the batch joins.
    /// </param>
    /// <param name="cancellationToken">
    /// An additional token linked to every started child and its descendants.
    /// </param>
    /// <returns>
    /// Source-ordered outcomes for started children; skipped items have no result.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="source"/> or <paramref name="child"/> is null, or a child description has no delegate.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="failureBehavior"/> is undefined.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A child is registered after the parent delegate has returned.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The parent or supplied token is canceled, or a child is canceled under Throw policy.
    /// </exception>
    /// <exception cref="Exception">
    /// An infrastructure or construction error occurs, or a child fails under Throw policy.
    /// </exception>
    /// <remarks>
    /// The sequence is enumerated once and factories run synchronously as each item is reached.
    /// Child delegates start in source order through their first asynchronous wait; asynchronous work
    /// may overlap. A null description skips an item. Ordinary child failures do not stop later siblings
    /// from starting. Throw waits for every started child before propagating failures. Record returns each
    /// child's outcome. Construction and infrastructure errors propagate after all committed children
    /// finish. The batch token cancels every started batch subtree and stops further registration without
    /// canceling the parent or unrelated operations. Parent or supplied-token cancellation always
    /// propagates after started children finish. Child-only cancellation is returned under Record and
    /// propagated under Throw. Skipped items have no result, and child failures remain recorded in the
    /// operation tree even if caught.
    /// </remarks>
    Task<OperationBatchResult> Children<T>(IEnumerable<T> source,
                                           Func<T, OperationChild> child,
                                           OperationFailureBehavior failureBehavior,
                                           CancellationToken cancellationToken);
}
