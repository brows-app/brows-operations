# Brows.Operations

Core interfaces and operation tracking for asynchronous .NET work. An `IOperator` starts operations; each `OperationDelegate` reports progress through `IOperationProgress` and can create child operations.

Progress and target changes from children roll up to the root operation. Failed operations remain in the collection for inspection, while successful roots are removed automatically. The concrete `Operator` implementation is internal; reference Brows.Operations.Composition and resolve `IOperatorFactory` through Brows.Composition to create an operator.

Each `Operate` call captures the current synchronization context for observable collection changes in the root and its descendants. Collection mutations dispatch synchronously to that context, or run on the calling thread when no context was captured. For UI binding, start work on the UI context. Progress reports and child registration may continue on worker threads, including after `ConfigureAwait(false)`; property-change notifications are not dispatched. Serialize state updates across the operation tree and avoid concurrent mutations while enumerating live collections. See the [repository README](https://github.com/brows-app/brows-operations#readme) for a usage example and build instructions.
