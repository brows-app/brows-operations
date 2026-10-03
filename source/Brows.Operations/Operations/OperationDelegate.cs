using System.Threading.Tasks;

namespace Brows.Operations;

/// <summary>Performs asynchronous work for an operation.</summary>
/// <param name="progress">Reports state and registers child operations while this delegate is running.</param>
/// <param name="token">The operation's cancellation token, linked to its parent's token for child operations.</param>
/// <returns>A task representing the delegate's work.</returns>
/// <remarks>
/// Progress reporting and child registration may continue on worker threads, including after
/// ConfigureAwait(false). Observable collection changes use the context captured by the root's
/// Operate call by default, or run on the thread making each change when synchronizeWithCurrentContext
/// is false or no context is available. This option does not change the delegate's execution context
/// or normal await context capture. Property-change notifications are not dispatched. Serialize
/// operation state updates across the tree and honor cancellation cooperatively. The operation records delegate failures and waits
/// for registered descendants even when this delegate fails or is canceled.
/// </remarks>
public delegate Task OperationDelegate(IOperationProgress progress, CancellationToken token);
