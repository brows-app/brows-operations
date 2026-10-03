# Brows.Operations

Core interfaces and operation tracking for asynchronous .NET work. An `IOperator` starts operations; each `OperationDelegate` reports progress through `IOperationProgress` and can create child operations.

Progress and target changes from children roll up to the root operation. Failed operations remain in the collection for inspection, while successful roots are removed automatically. The concrete `Operator` implementation is internal; reference Brows.Operations.Composition and resolve `IOperatorFactory` through Brows.Composition to create an operator.

An operator is affine to the synchronization context current when it is created. Operation state changes, property-change notifications, and observable collection changes run on that context. `Operate`, progress reporting, child registration, and cancellation can be called from any thread, including after `ConfigureAwait(false)`; calls from other threads are posted to the context, and worker progress reports are coalesced. Read operation state on the context; `IOperationCollection.Snapshot()` and `Count` are safe from any thread. Without a context, changes run on the calling thread under an operator lock. See the [repository README](https://github.com/brows-app/brows-operations#readme) for the full threading contract, a usage example, and build instructions.
