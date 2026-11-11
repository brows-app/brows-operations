# Brows.Operations

Core interfaces and operation tracking for asynchronous .NET work. An `IOperator` starts operations; each `OperationDelegate` reports progress through `IOperationProgress` and can create child operations.

Progress and target changes from children roll up to the root operation. Failed operations remain in the collection for inspection, while successful roots are removed automatically. The concrete `Operator` implementation is internal; reference Brows.Operations.Composition and resolve `IOperatorFactory` through Brows.Composition to create an operator.

For byte-valued progress, pass `kind: OperationProgressKind.FileSize` to `IOperationProgress.Change`.
The default progress and target strings then use B, KiB, MiB, and larger binary units. Set
`progressStringFormat` or `targetStringFormat` to customize the numeric portion of scaled values; counts
below 1 KiB remain whole bytes. Pass `formatProvider` to choose the number-formatting culture. Null
formatting arguments leave their existing settings unchanged.

The WPF operation view can display `ProgressRate` and `ProgressRateAverage`, in progress units per
second. The current rate samples the net change since the previous sample about once per second. The
average is the net progress change since the operation started divided by elapsed lifetime, including
idle time. Progress corrections can make either value negative. The bindable runtime properties raise
`PropertyChanged` when a sample changes their value. When the operation and its descendants finish, the
current rate becomes zero and the average freezes at its final value. File-size progress uses bytes per
second. These display properties are not members of the public `IOperation` interface.

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
