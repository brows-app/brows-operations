# Brows.Operations

Core interfaces and operation tracking for asynchronous .NET work. An `IOperator` starts operations; each `OperationDelegate` reports progress through `IOperationProgress` and can create child operations.

Progress and target changes from children roll up to the root operation. Failed operations remain in the collection for inspection, while successful roots are removed automatically. The concrete `Operator` implementation is internal; reference Brows.Operations.Composition and resolve `IOperatorFactory` through Brows.Composition to create an operator.

## Child outcomes

`IOperationProgress.Child` and `Children` require an `OperationFailureBehavior` and an additional
`CancellationToken`. Pass `default` when no additional token is needed. A child inherits its parent's
cancellation token; a supplied child token is linked through that child's descendants without canceling
the parent or its siblings.

Use `Throw` when later work depends on a child succeeding. It propagates ordinary failures and child-only
cancellation after the child delegate, asynchronous cleanup, and registered descendants finish. Use
`Record` for independent work and inspect the returned `OperationResult`. A `Children` call returns an
`OperationBatchResult` with source-ordered outcomes and counts; null factory results skip items and have
no result. A batch waits for all started siblings after a failure. Its supplied token cancels every started
batch child and stops further registration. Parent or supplied-token cancellation and infrastructure errors
propagate under either policy. `Record` records ordinary work failures and returns their outcomes;
`Throw` records the same failures and propagates them after joining. One distinct failure keeps its
original exception, while multiple distinct failures are represented by an aggregate. A descendant
failure affects its enclosing child outcome, and ordinary failure takes precedence over child-only
cancellation. Parent or supplied-token cancellation takes precedence over work outcomes and propagates
after joining; work failures remain recorded on the operation tree. Infrastructure errors take precedence
over ambient cancellation. Caught work failures remain recorded on the operation tree.

Keep ownership of staged resources until awaiting `Child` returns a result whose `Kind` is
`OperationResultKind.Succeeded`. The returned task includes asynchronous cleanup and disposal represented
by the delegate's task.

For byte-valued progress, pass `kind: OperationProgressKind.FileSize` to `IOperationProgress.Change`.
The default progress and target strings then use B, KiB, MiB, and larger binary units. Set
`progressFormat` or `targetFormat` to customize the numeric portion of scaled values; counts
below 1 KiB remain whole bytes. Pass `formatProvider` to choose the number-formatting culture. Null
formatting arguments leave their existing settings unchanged.

The WPF operation view can display `ProgressRate` and `ProgressRateAverage`, in progress units per
second. The current rate samples the net change since the previous sample about once per second. The
average is the net progress change since the operation started divided by elapsed lifetime, including
idle time. Progress corrections can make either value negative. The bindable runtime properties raise
`PropertyChanged` when a sample changes their value. When the operation and its descendants finish, the
current rate becomes zero and the average freezes at its final value. File-size rate strings use the same
formatting as file-size progress, automatically scaling to B, KiB, MiB, and larger binary units. Rate strings
contain the formatted value and unit without a `/s` suffix. These display properties are not members of the
public `IOperation` interface.

`IOperatorFactory.Create()` captures the current synchronization context when the operator is created.
For WPF binding, create the operator on the dispatcher thread or pass its context explicitly to
`Create(SynchronizationContext)`. Root and child collection changes are posted asynchronously to the
captured context. With no context, the producer that starts a queue drain projects changes
synchronously. A concurrent or reentrant update can enqueue while that drain is active, return before
its observable projection, and have its event delivered on the active drainer thread. Progress reports
and child registration may run on worker threads, including after `ConfigureAwait(false)`.
Operation state updates are synchronized by the library. Property-change notifications run on the
updating thread after state locks have been released. State is committed before notifications, and
reentrant reports append notifications to the current thread's batch. WPF bindings marshal bound
property updates to their dispatcher. Use `IOperationCollection.Snapshot()` to enumerate roots while
other threads add or remove them. See the
[repository README](https://github.com/brows-app/brows-operations#readme) for a usage example and build
instructions.
