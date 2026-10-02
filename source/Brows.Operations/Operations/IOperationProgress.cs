using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Brows.Operations;

/// <summary>Reports operation state and starts child operations.</summary>
/// <remarks>
/// Use this interface on the operation's UI synchronization context. Register children before
/// the operation's delegate returns; the operation waits for all registered descendants.
/// </remarks>
public interface IOperationProgress {
    /// <summary>Updates numeric progress, display strings, and operation metadata.</summary>
    /// <param name="addProgress">An amount to add to progress after applying <paramref name="setProgress"/>.</param>
    /// <param name="setProgress">An absolute progress value, or null to leave it unchanged before any addition.</param>
    /// <param name="addTarget">An amount to add to the target after applying <paramref name="setTarget"/>.</param>
    /// <param name="setTarget">An absolute target value, or null to leave it unchanged before any addition.</param>
    /// <param name="progressString">A progress display string, or null to use the numeric progress value.</param>
    /// <param name="targetString">A target display string, or null to use the numeric target value.</param>
    /// <param name="name">A new display name, or null to leave the name unchanged.</param>
    /// <param name="data">New detail text, or null to leave the detail text unchanged.</param>
    /// <remarks>
    /// Numeric values represent caller-defined units. Changes are aggregated into each ancestor;
    /// setting a child's value adjusts ancestors by the difference. Set values are applied before
    /// additions, and numeric values throughout the hierarchy are committed before their notifications.
    /// Every call replaces both display strings, including calls that only update metadata.
    /// A numeric report starts a delay of about one second, after which still-running work becomes
    /// relevant for display. Errors become relevant immediately.
    /// </remarks>
    void Change(long? addProgress = null,
                long? setProgress = null,
                long? addTarget = null,
                long? setTarget = null,
                string progressString = null,
                string targetString = null,
                string name = null,
                string data = null);

    /// <summary>Starts a child operation and waits for it and its registered descendants to finish.</summary>
    /// <param name="name">The child's initial display name.</param>
    /// <param name="task">The delegate that performs the child's work.</param>
    /// <returns>A task that completes when the child's operation lifetime ends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The parent delegate has already returned.</exception>
    /// <exception cref="OperationCanceledException">Cancellation has already been requested for the parent.</exception>
    /// <remarks>
    /// The child's delegate starts immediately. Its ordinary failures are recorded on the child
    /// and contribute to the parent's error state, but do not fault the returned task.
    /// Successful awaiting indicates completion, rather than successful work. Registration failures
    /// and unexpected completion-task failures propagate through the returned task.
    /// </remarks>
    Task Child(string name, OperationDelegate task);

    /// <summary>Starts children from a sequence and waits for their operation lifetimes to end.</summary>
    /// <typeparam name="T">The type of source item used to describe each child.</typeparam>
    /// <param name="source">The items to enumerate for child creation.</param>
    /// <param name="child">A factory that returns a child description, or null to skip an item.</param>
    /// <returns>
    /// A task whose result is <see langword="true"/> if at least one child task was created, or
    /// <see langword="false"/> if the sequence was empty or every item was skipped.
    /// The result does not indicate whether the children succeeded.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="child"/> is null.</exception>
    /// <exception cref="ArgumentNullException">A child description has a null work delegate.</exception>
    /// <exception cref="InvalidOperationException">A child is registered after the parent delegate has returned.</exception>
    /// <exception cref="OperationCanceledException">A child is registered after cancellation is requested for the parent.</exception>
    /// <remarks>
    /// The sequence and child factories are evaluated eagerly, and each delegate starts synchronously
    /// through its first asynchronous wait. All children are scheduled before this method awaits them, so
    /// asynchronous work may overlap. Child delegate failures are recorded without faulting this
    /// task. Enumeration, factory, registration, and unexpected completion-task failures propagate;
    /// the parent operation still joins children already registered before completing.
    /// </remarks>
    Task<bool> Children<T>(IEnumerable<T> source, Func<T, OperationChild> child);
}
