# Brows.Operations

Core interfaces and operation tracking for asynchronous .NET work. An `IOperator` starts operations; each `OperationDelegate` reports progress through `IOperationProgress` and can create child operations.

Progress and target changes from children roll up to the root operation. Failed operations remain in the collection for inspection, while successful roots are removed automatically. The concrete `Operator` implementation is internal; reference Brows.Operations.Composition and resolve `IOperatorFactory` through Brows.Composition to create an operator.

The implementation assumes a single-threaded UI synchronization context. Start work on the UI context and continue reporting progress on that context so changes and notifications run sequentially. See the [repository README](https://github.com/brows-app/brows-operations#readme) for a usage example and build instructions.
