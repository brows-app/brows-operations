# Brows.Operations.Composition

Composition integration for Brows.Operations. This package provides `IOperatorFactory` as a Brows.Composition export. Import the factory through your composition environment and call `Create` to obtain an `IOperator`.

Given an initialized Brows.Composition import result named `imported`:

```csharp
using Brows.Composition;
using Brows.Operations;

IOperatorFactory factory = imported.Find<IOperatorFactory>();
IOperator operations = factory.Create();
```

The returned operator uses the core Brows.Operations implementation. `Create()` captures the current
synchronization context when the operator is created. In a WPF application, call it on the dispatcher
thread, or pass the dispatcher context to `Create(SynchronizationContext)` explicitly. Root and child
collection changes are posted asynchronously to that context; when no context was captured, changes
run on the thread making each update. Progress reports and child registration may run on worker threads.
Operation state updates are synchronized by the library, while property-change notifications run on
the updating thread and WPF bindings marshal bound property updates to the dispatcher. Creating an
operator does not change delegate execution or normal `await` context capture. See the
[repository](https://github.com/brows-app/brows-operations) for the sample project and usage guidance.
