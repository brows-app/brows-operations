# Brows.Operations.Composition

Composition integration for Brows.Operations. This package provides `IOperatorFactory` as a Brows.Composition export. Import the factory through your composition environment and call `Create` to obtain an `IOperator`.

Given an initialized Brows.Composition import result named `imported`:

```csharp
using Brows.Composition;
using Brows.Operations;

IOperatorFactory factory = imported.Find<IOperatorFactory>();
IOperator operations = factory.Create();
```

The returned operator uses the core Brows.Operations implementation. Work should be started and progress reported on a single-threaded UI synchronization context. See the [repository](https://github.com/brows-app/brows-operations) for the sample project and usage guidance.
