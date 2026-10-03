using System.Threading;

namespace Brows.Operations;

internal sealed class Operator : IOperator {
    private readonly OperationCollection OperationCollection = new();
    private readonly SynchronizationContext SynchronizationContext;

    public IOperationCollection Operations =>
        OperationCollection;

    public Operator(SynchronizationContext synchronizationContext = null) {
        SynchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
    }

    public void Operate(string name, OperationDelegate task) {
        var manager = new OperationManager(OperationCollection, SynchronizationContext);
        manager.Operate(name, task);
    }
}
