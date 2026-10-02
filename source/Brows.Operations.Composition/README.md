# Brows.Operations.Composition

Composition integration for Brows.Operations. This package provides `IOperatorFactory` as a Brows.Composition export. Import the factory through your composition environment and call `Create` to obtain an `IOperator`.

Given an initialized Brows.Composition import result named `imported`:

```csharp
using Brows.Composition;
using Brows.Operations;

IOperatorFactory factory = imported.Find<IOperatorFactory>();
IOperator operations = factory.Create();
```

The returned operator uses the core Brows.Operations implementation. `Create` does not capture a synchronization context; each `Operate` call captures the current context for observable collection changes in that root and its descendants. For UI binding, call `Operate` on the UI context. Collection mutations dispatch synchronously to it, or run on the calling thread if no context was captured. Progress reports and child registration may run on worker threads, while property-change notifications are not dispatched. Serialize state updates across the operation tree. See the [repository](https://github.com/brows-app/brows-operations) for the sample project and usage guidance.
