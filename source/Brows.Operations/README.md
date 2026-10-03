# Brows.Operations

Core interfaces and operation tracking for asynchronous .NET work. An `IOperator` starts operations; each `OperationDelegate` reports progress through `IOperationProgress` and can create child operations.

Progress and target changes from children roll up to the root operation. Failed operations remain in the collection for inspection, while successful roots are removed automatically. The concrete `Operator` implementation is internal; reference Brows.Operations.Composition and resolve `IOperatorFactory` through Brows.Composition to create an operator.

`IOperatorFactory.Create()` captures the current synchronization context when the operator is created.
For WPF binding, create the operator on the dispatcher thread or pass its context explicitly to
`Create(SynchronizationContext)`. Root and child collection changes are posted asynchronously to the
captured context; if no context was captured, changes run on the thread making each update. Progress
reports and child registration may run on worker threads, including after `ConfigureAwait(false)`.
Operation state updates are synchronized by the library. Property-change notifications run on the
updating thread after state locks have been released. State is committed before notifications, and
reentrant reports append notifications to the current thread's batch. WPF bindings marshal bound
property updates to their dispatcher. Use `IOperationCollection.Snapshot()` to enumerate roots while
other threads add or remove them. See the
[repository README](https://github.com/brows-app/brows-operations#readme) for a usage example and build
instructions.
