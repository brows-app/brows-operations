using System.Threading;

namespace Brows.Operations;

internal sealed class Operator : IOperator {
    private readonly OperationCollection OperationCollection = new();

    public IOperationCollection Operations =>
        OperationCollection;

    public void Operate(string name, OperationDelegate task, bool synchronizeWithCurrentContext) {
        var
        manager = new OperationManager(OperationCollection, synchronizeWithCurrentContext
            ? SynchronizationContext.Current
            : null);
        manager.Operate(name, task);
    }
}
