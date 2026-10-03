# Brows.Operations.Composition

Composition integration for Brows.Operations. This package provides `IOperatorFactory` as a Brows.Composition export. Import the factory through your composition environment and call `Create` to obtain an `IOperator`.

Given an initialized Brows.Composition import result named `imported`:

```csharp
using Brows.Composition;
using Brows.Operations;

IOperatorFactory factory = imported.Find<IOperatorFactory>();
IOperator operations = factory.Create();
```

The returned operator uses the core Brows.Operations implementation. `Create` captures the current synchronization context; operation state changes, notifications, and collection changes run on it, while work can report progress from any thread. For UI binding, call `Create` on the UI thread. See the [repository](https://github.com/brows-app/brows-operations#threading) for the threading contract, the sample project, and usage guidance.
