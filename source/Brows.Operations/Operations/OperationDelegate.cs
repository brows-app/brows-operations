using System.Threading.Tasks;

namespace Brows.Operations;

/// <summary>Performs asynchronous work for an operation.</summary>
/// <param name="progress">Reports state and registers child operations while this delegate is running.</param>
/// <param name="token">The operation's cancellation token, linked to its parent's token for child operations.</param>
/// <returns>A task representing the delegate's work.</returns>
/// <remarks>
/// Progress reporting and child registration can continue on worker threads, including after
/// ConfigureAwait(false); those calls are posted to the operator's synchronization context. See
/// <see cref="IOperator"/>. Honor cancellation cooperatively. The operation records delegate failures and waits
/// for registered descendants even when this delegate fails or is canceled.
/// </remarks>
public delegate Task OperationDelegate(IOperationProgress progress, CancellationToken token);
