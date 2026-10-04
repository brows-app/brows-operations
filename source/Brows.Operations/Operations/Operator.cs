using System.Threading;

namespace Brows.Operations;

internal sealed class Operator : IOperator {
    private readonly OperationCollection OperationCollection;

    public IOperationCollection Operations =>
        OperationCollection;

    public Operator(SynchronizationContext synchronizationContext = null) {
        OperationCollection = new OperationCollection(new OperationSynchronization(
            synchronizationContext ?? SynchronizationContext.Current));
    }

    public void Operate(string name, OperationDelegate task) {
        var manager = new OperationManager(OperationCollection);
        manager.Operate(name, task);
    }
}
