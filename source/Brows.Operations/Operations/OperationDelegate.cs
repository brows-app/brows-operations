using System.Threading.Tasks;

namespace Brows.Operations;

/// <summary>
/// Performs asynchronous work for an operation.
/// </summary>
/// <param name="progress">
/// Reports state and registers child operations while this delegate is running.
/// </param>
/// <param name="token">
/// The operation's effective token, linked to its parent's token and any token supplied when it was registered.
/// </param>
/// <returns>
/// A task representing the delegate's work.
/// </returns>
/// <remarks>
/// Progress reporting and child registration may continue on worker threads, including after
/// <see cref="Task.ConfigureAwait(bool)"/>. The operator posts observable collection changes to the
/// context captured when it was created. Without a captured context, a producer that starts a
/// serialized queue drain projects changes synchronously; concurrent or reentrant producers can
/// return before their queued change is projected. Property-change notifications run on the thread
/// updating state; WPF bindings marshal bound property updates to their dispatcher. Operation state
/// updates are synchronized across the tree. Honor cancellation cooperatively. An operation's
/// lifetime includes cleanup and disposal represented by this delegate's returned task, and the
/// operation waits for registered descendants even when this delegate fails or is canceled.
/// </remarks>
public delegate Task OperationDelegate(IOperationProgress progress, CancellationToken token);
